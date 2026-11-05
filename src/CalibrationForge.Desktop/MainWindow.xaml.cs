using System.ComponentModel;
using System.IO;
using System.Windows;
using CalibrationForge.Core;
using Microsoft.Win32;

namespace CalibrationForge.Desktop;

public partial class MainWindow : Window
{
    private readonly SimulatorViewModel model = new(new SyntheticCalibrationWorkspace(new SimulatedCalibrationDevice()));
    private readonly BinEditorViewModel binModel;
    private readonly DiagnosticsViewModel diagnosticsModel = new();
    private readonly ToolsViewModel toolsModel = new();
    private CancellationTokenSource? operation;

    public MainWindow() : this(new BinWorkspace()) { }

    public MainWindow(BinWorkspace binWorkspace)
    {
        binModel = new BinEditorViewModel(binWorkspace);
        InitializeComponent();
        DataContext = model;
        BinEditorHost.Content = new BinEditorControl(binModel);
        DiagnosticsHost.Content = new DiagnosticsControl(diagnosticsModel);
        ToolsHost.Content = new ToolsControl(toolsModel);
    }

    private async void ReadSimulator_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanStart || !ConfirmReplace()) { return; }
        await RunOperationAsync("Reading simulator…", model.ReadAsync);
    }

    private async void WriteSimulator_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanSaveOrWrite) { return; }
        await RunOperationAsync("Writing simulator, then independently reading back all bytes…", model.WriteAsync);
    }

    private async void LoadImage_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanStart) { return; }
        var dialog = new OpenFileDialog
        {
            Title = "Load 16-byte simulator demo (not an ECM BIN)",
            Filter = "16-byte simulator demo (*.bin)|*.bin",
            DefaultExt = ".bin",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true || !ConfirmReplace()) { return; }
        await RunOperationAsync("Loading and validating synthetic image…", token => model.LoadAsync(dialog.FileName, token));
    }

    private async void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanSaveOrWrite) { return; }
        var dialog = new SaveFileDialog
        {
            Title = "Save 16-byte simulator demo (not an ECM BIN)",
            Filter = "16-byte simulator demo (*.bin)|*.bin",
            DefaultExt = ".bin",
            FileName = "calibrationforge-synthetic.bin",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) { return; }
        await RunOperationAsync("Saving current edits locally…", token => model.SaveAsync(dialog.FileName, token));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();

    private async Task RunOperationAsync(string message, Func<CancellationToken, Task> action)
    {
        if (!model.CanStart) { return; }
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        model.BeginOperation(message);
        try
        {
            await action(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            model.ReportError("Operation canceled; completion is not confirmed. Edits retained. A write or file save may have occurred; read back or reload to check.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            model.ReportError($"Error: {ex.Message} Current edits are retained; completion is not confirmed.");
        }
        catch (Exception ex)
        {
            model.ReportError($"Unexpected {ex.GetType().Name}: {ex.GetBaseException().Message} Completion is not confirmed; retry or restart the simulator.");
        }
        finally
        {
            operation = null;
            model.EndOperation();
        }
    }

    private bool ConfirmReplace() => !model.HasUnsavedChanges ||
        MessageBox.Show(this, "Replace current unsaved edits? A failed read or invalid load will retain them.",
            "Unsaved synthetic changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (toolsModel.IsBusy)
        {
            toolsModel.Cancel();
            e.Cancel = true;
            return;
        }
        if (diagnosticsModel.IsBusy)
        {
            diagnosticsModel.ReportError("Wait for diagnostic import or export to finish before closing.");
            e.Cancel = true;
            return;
        }
        if (binModel.IsBusy)
        {
            binModel.ReportError("Wait for BIN loading or export to finish before closing.");
            e.Cancel = true;
            return;
        }
        if (operation is not null)
        {
            operation.Cancel();
            model.ReportError("Cancellation requested. Wait for the current operation to finish, then close again.");
            e.Cancel = true;
            return;
        }

        if (model.HasUnsavedChanges &&
            MessageBox.Show(this, "Exit without saving current synthetic edits?", "Unsaved synthetic changes",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }

        if (!e.Cancel && binModel.HasUnsavedChanges &&
            MessageBox.Show(this, "Exit without exporting unsaved BIN edits?", "Unsaved BIN changes",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
    }
}
