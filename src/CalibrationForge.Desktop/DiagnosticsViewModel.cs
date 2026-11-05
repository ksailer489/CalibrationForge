using System.ComponentModel;
using System.Globalization;
using CalibrationForge.Core;

namespace CalibrationForge.Desktop;

public sealed class DiagnosticsViewModel : INotifyPropertyChanged
{
    private DiagnosticReport? report;
    private DiagnosticModule? selectedModule;
    private bool busy;
    private string status = "Load the example or import a capture. No vehicle is connected.";

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<DiagnosticModule> Modules => report?.Modules ?? Array.Empty<DiagnosticModule>();
    public IReadOnlyList<DiagnosticCode> Codes => selectedModule?.Codes ?? Array.Empty<DiagnosticCode>();
    public IReadOnlyList<ReadinessMonitor> Monitors => selectedModule?.Monitor?.Monitors ?? Array.Empty<ReadinessMonitor>();
    public string Status => status;
    public bool IsBusy => busy;
    public bool CanLoad => !busy;
    public bool CanExport => !busy && report is not null;
    public string Source => report is null ? "No report loaded"
        : $"{(report.IsSimulation ? "SIMULATED DATA" : "CAPTURED RESPONSES")} - {report.Source}";
    public string CaptureTime => report is null ? "Not recorded"
        : report.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    public string LampStatus => selectedModule?.LampStatus ?? "Not recorded";
    public string Coverage => selectedModule?.Coverage ?? "No controller response selected. Vehicle condition is unknown.";
    public string CodeSummary => selectedModule is null ? "No controller selected"
        : $"{Codes.Count} code entries" + (selectedModule.MissingResponses.Count == 0 ? "" : " - incomplete capture");
    public string MonitorSummary => selectedModule?.Monitor is not { } monitor ? "Readiness not recorded"
        : $"{monitor.Monitors.Count(item => item.State == ReadinessState.Complete)} complete / " +
          $"{monitor.Monitors.Count(item => item.State != ReadinessState.NotSupported)} supported monitors";
    public string ExampleCapture => DiagnosticCapture.ExampleJson;

    public DiagnosticModule? SelectedModule
    {
        get => selectedModule;
        set
        {
            if (busy || ReferenceEquals(value, selectedModule)) { return; }
            if (value is not null && !Modules.Contains(value)) { throw new ArgumentException("Select a module from this report."); }
            selectedModule = value;
            Refresh();
        }
    }

    public void LoadDemo()
    {
        EnsureIdle();
        busy = true;
        try
        {
            SetReport(DiagnosticCapture.CreateDemo());
            status = "Example loaded. These are simulated faults, not readings from your vehicle.";
        }
        finally
        {
            busy = false;
            Refresh();
        }
    }

    public async Task ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureIdle();
        busy = true;
        report = null;
        selectedModule = null;
        status = "Importing captured responses. No vehicle connection.";
        Refresh();
        var completed = false;
        try
        {
            var candidate = await DiagnosticCapture.LoadAsync(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetReport(candidate);
            status = candidate.Modules.Count == 0 ? "Capture contains no controller responses. Vehicle condition is unknown."
                : "Capture imported. Results describe the file, not the vehicle's current condition.";
            completed = true;
        }
        finally
        {
            if (!completed) { status = "Import did not complete; no diagnostic report is loaded."; }
            busy = false;
            Refresh();
        }
    }

    public async Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureIdle();
        var snapshot = report ?? throw new InvalidOperationException("Load a diagnostic report before exporting.");
        busy = true;
        status = "Exporting offline diagnostic report...";
        Refresh();
        var completed = false;
        try
        {
            await DiagnosticReportFile.SaveAsync(path, snapshot, cancellationToken);
            status = $"Diagnostic report saved to {path}. No vehicle commands were sent.";
            completed = true;
        }
        finally
        {
            if (!completed) { status = "Export did not complete. The loaded report is retained."; }
            busy = false;
            Refresh();
        }
    }

    public void ReportError(string message)
    {
        status = message;
        Refresh();
    }

    private void SetReport(DiagnosticReport candidate)
    {
        report = candidate;
        selectedModule = candidate.Modules.FirstOrDefault();
        Refresh();
    }

    private void EnsureIdle()
    {
        if (busy) { throw new InvalidOperationException("Wait for the current diagnostic operation to finish."); }
    }

    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
