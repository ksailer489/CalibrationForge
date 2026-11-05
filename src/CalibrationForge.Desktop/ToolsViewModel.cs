using System.ComponentModel;
using CalibrationForge.Core;

namespace CalibrationForge.Desktop;

public sealed class ToolsViewModel : INotifyPropertyChanged
{
    private readonly IOfflineWorkflowService service;
    private CancellationTokenSource? operation;
    private string result = "";
    private string provenance = "No source loaded. Identity, memory coverage, and checksum validity are unknown.";
    private string status = "Choose an offline tool. Originals are never modified.";
    private double progress;

    public ToolsViewModel(IOfflineWorkflowService? service = null) => this.service = service ?? new OfflineWorkflowService();
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsBusy => operation is not null;
    public bool CanStart => !IsBusy;
    public string Result => result;
    public string Provenance => provenance;
    public string Status => status;
    public double Progress => progress;

    public Task IdentityAsync(string? tracePath = null) => RunAsync("Replaying ordered CAN identity transactions…", async token =>
    {
        var replay = await service.ReadIdentityAsync(tracePath, token);
        return (
            $"{(replay.IsSimulation ? "SIMULATED CAN TRAFFIC — not a vehicle scan" : "RECORDED CAN TRAFFIC — provenance not independently verified")} · {replay.Source}",
            $"Request / response: {replay.Identity.RequestId:X3} / {replay.Identity.ResponseId:X3}\n" +
            $"Reported VIN: {replay.Identity.Vin ?? "Not supported"}\n" +
            $"Calibration IDs: {(replay.Identity.CalibrationIds is null ? "Not supported" : string.Join(", ", replay.Identity.CalibrationIds))}\n" +
            $"Reported CVNs: {(replay.Identity.CalibrationVerificationNumbers is null ? "Not supported" : string.Join(", ", replay.Identity.CalibrationVerificationNumbers.Select(value => value.ToString("X8"))))}\n" +
            "Complete ordered trace matched. Adapter timing, identity authenticity, E92 compatibility and checksum validity remain unknown.");
    });

    public Task VerifyFileAsync(string source, string output) => RunAsync("Reading file twice; comparing before atomic ZIP export…", async token =>
    {
        var current = operation;
        var reporter = new Progress<MemoryReadProgress>(value =>
        {
            if (!ReferenceEquals(current, operation) || token.IsCancellationRequested) { return; }
            progress = ((value.Pass - 1) + (double)value.CompletedBytes / value.TotalBytes) * 50;
            Refresh();
        });
        var dump = await service.VerifyFileAsync(source, output, reporter, token);
        return ($"FILE · {dump.Source.Name} · session {dump.Source.SessionId} · not an ECM read",
            $"Two file reads matched {dump.Plan.ReadableBytes:N0} bytes.\nUnknown bytes in requested range: {dump.Plan.Region.Length - dump.Plan.ReadableBytes:N0}\n" +
            $"SHA256: {dump.Sha256}\nZIP saved: {output}\nMatching reads establish consistency only; source authenticity, full-ECM coverage and flash readiness are unknown.");
    });

    public Task AnalyzeAsync(string reference, string? candidate) => RunAsync("Checking exact reference and observed additive regions…", async token =>
    {
        var report = await service.AnalyzeChecksumsAsync(reference, candidate, token);
        return ("EXACT SHA-BOUND PUBLIC E92 REFERENCE · vehicle provenance unknown · read-only analysis",
            report.Profile + "\n" + string.Join("\n", report.Regions.Select(region =>
                $"{region.Name}: stored {region.Stored:X4}, expected {region.Expected:X4}, residual {region.Residual:X4}, changed bytes {region.ChangedBytes}")) +
            $"\nChanged bytes outside observed coverage: {report.ChangesOutsideCoverage}\n{report.Evidence}\n" +
            "Observed additive checks only. OS, secondary checksums/CVN and vehicle validation unresolved. No repair; NOT flash-ready.");
    });

    public Task ExportLogAsync(string path) => RunAsync("Exporting metadata-only support log…", async token =>
    {
        await service.Journal.ExportAsync(path, token);
        return ("SUPPORT METADATA ONLY · no VIN, paths, payloads or exception messages",
            "Support log saved to a new JSON file. Includes operation, time, duration, outcome and exception type only.");
    });

    public void Cancel() => operation?.Cancel();

    private async Task RunAsync(string message, Func<CancellationToken, Task<(string Provenance, string Result)>> action)
    {
        if (IsBusy) { throw new InvalidOperationException("Wait for the current tool or cancel it first."); }
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        status = message;
        progress = 0;
        result = "";
        provenance = "Operation in progress — no new result confirmed.";
        Refresh();
        try
        {
            var candidate = await action(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            (provenance, result) = candidate;
            progress = 100;
            status = "Completed offline. Originals preserved.";
        }
        catch (OperationCanceledException)
        {
            status = "Canceled. No result confirmed; originals preserved. Check destination if publication had already completed.";
            provenance = "CANCELED · no validity established";
        }
        catch (Exception ex)
        {
            status = $"{ex.GetType().Name}: {ex.Message} No result confirmed; originals preserved.";
            provenance = "FAILED · identity, coverage and checksum validity not established";
        }
        finally
        {
            operation = null;
            Refresh();
        }
    }

    private void Refresh()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(CanStart), nameof(Result), nameof(Provenance), nameof(Status), nameof(Progress) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
