using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using CalibrationForge.Core;

namespace CalibrationForge.Desktop;

public sealed class SimulatorViewModel : INotifyPropertyChanged
{
    private readonly SyntheticCalibrationWorkspace workspace;
    private string valueText = "";
    private string validationMessage = "";
    private string status = "Read simulator to begin, or load a raw synthetic .bin image.";
    private string source = "No image loaded";
    private bool busy;

    public SimulatorViewModel(SyntheticCalibrationWorkspace workspace)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ValueText
    {
        get => valueText;
        set
        {
            if (busy || !workspace.IsLoaded || valueText == value) { return; }
            valueText = value;
            validationMessage = "";
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                validationMessage = $"Enter a whole number from {SyntheticCalibration.DemoLevel.Minimum} to {SyntheticCalibration.DemoLevel.Maximum}.";
            }
            else
            {
                try { workspace.EditDemoLevel(parsed); }
                catch (ArgumentException ex) { validationMessage = ex.Message; }
            }

            status = validationMessage.Length == 0
                ? $"Synthetic DemoLevel set to {workspace.DemoLevel}. Save locally or write and verify in the simulator."
                : "Invalid edit. Correct DemoLevel before saving or writing.";
            Refresh();
        }
    }

    public string Status => status;
    public string ValidationMessage => validationMessage;
    public string Source => source;
    public string ParameterLabel => $"{SyntheticCalibration.DemoLevel.Name} ({SyntheticCalibration.DemoLevel.Minimum}–{SyntheticCalibration.DemoLevel.Maximum})";
    public bool CanStart => !busy;
    public bool CanEdit => !busy && workspace.IsLoaded;
    public bool CanSaveOrWrite => CanEdit && validationMessage.Length == 0;
    public bool CanCancel => busy;
    public bool HasUnsavedChanges => workspace.IsDirty || validationMessage.Length != 0;
    public string DirtyState => !workspace.IsLoaded ? "No image loaded" : HasUnsavedChanges ? "Unsaved changes" : "No pending changes";
    public string ImageIdentity => workspace.IsLoaded ? $"Synthetic demo image • {SyntheticCalibration.ImageLength} bytes • raw .bin" : "Synthetic demo image • not read or loaded";
    public string ImageHash => workspace.IsLoaded ? Convert.ToHexString(SHA256.HashData(workspace.ExportImage())) : "Read or load an image to compute SHA256";
    public string Changes => !workspace.IsLoaded ? "No edits yet."
        : validationMessage.Length != 0 ? "Invalid draft; last valid image retained. Save and write are disabled."
        : workspace.IsDirty ? $"DemoLevel: {workspace.BaselineDemoLevel} → {workspace.DemoLevel}"
        : $"DemoLevel: {workspace.DemoLevel} • current image matches its read, loaded, saved, or verified baseline.";

    public void BeginOperation(string message)
    {
        if (busy) { throw new InvalidOperationException("Wait for the current operation to finish."); }
        busy = true;
        status = message;
        Refresh();
    }

    public void EndOperation()
    {
        busy = false;
        Refresh();
    }

    public void ReportError(string message)
    {
        status = message;
        Refresh();
    }

    public async Task ReadAsync(CancellationToken cancellationToken)
    {
        await workspace.ReadSimulatorAsync(cancellationToken);
        source = "Read from in-memory simulator";
        SynchronizeValue();
        status = $"Simulator read complete. Synthetic DemoLevel = {workspace.DemoLevel}.";
        Refresh();
    }

    public async Task WriteAsync(CancellationToken cancellationToken)
    {
        EnsureValidEdit();
        await workspace.WriteAndVerifyAsync(cancellationToken);
        SynchronizeValue();
        status = $"Simulator write verified: DemoLevel = {workspace.DemoLevel}. All {SyntheticCalibration.ImageLength} readback bytes match.";
        Refresh();
    }

    public async Task LoadAsync(string path, CancellationToken cancellationToken)
    {
        var candidate = await SyntheticImageFile.LoadAsync(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        workspace.LoadImage(candidate);
        source = path;
        SynchronizeValue();
        status = $"Loaded synthetic image. DemoLevel = {workspace.DemoLevel}. Simulator unchanged.";
        Refresh();
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken)
    {
        EnsureValidEdit();
        var snapshot = workspace.ExportImage();
        await SyntheticImageFile.SaveAsync(path, snapshot, cancellationToken);
        workspace.MarkSaved(snapshot);
        status = $"Saved {SyntheticCalibration.ImageLength}-byte raw synthetic image to {path}. Simulator unchanged.";
        Refresh();
    }

    private void EnsureValidEdit()
    {
        if (validationMessage.Length != 0) { throw new InvalidOperationException("Correct DemoLevel before saving or writing."); }
    }

    private void SynchronizeValue()
    {
        valueText = workspace.DemoLevel.ToString(CultureInfo.InvariantCulture);
        validationMessage = "";
    }

    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
