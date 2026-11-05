using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CalibrationForge.Desktop;

public partial class DiagnosticsControl : UserControl
{
    private readonly DiagnosticsViewModel model;
    private CancellationTokenSource? operation;

    public DiagnosticsControl(DiagnosticsViewModel model)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
    }

    private void Demo_Click(object sender, RoutedEventArgs e)
    {
        if (model.CanLoad) { model.LoadDemo(); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanLoad) { return; }
        var dialog = new OpenFileDialog
        {
            Title = "Import offline OBD response capture (not a BIN)",
            Filter = "CalibrationForge OBD capture (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) { return; }
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        try { await model.ImportAsync(dialog.FileName, cancellation.Token); }
        catch (OperationCanceledException) { model.ReportError("Import canceled. No diagnostic report loaded."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException or ArgumentException)
        {
            model.ReportError($"Capture import failed: {ex.Message} No diagnostic report loaded.");
        }
        catch (Exception ex) { model.ReportError($"Unexpected {ex.GetType().Name}: {ex.Message} Import is not confirmed."); }
        finally { operation = null; }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanExport) { return; }
        var dialog = new SaveFileDialog
        {
            Title = "Export offline diagnostic report to a new file",
            Filter = "Diagnostic report (*.txt)|*.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = "calibrationforge-diagnostics.txt",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) { return; }
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        try { await model.ExportAsync(dialog.FileName, cancellation.Token); }
        catch (OperationCanceledException) { model.ReportError("Export canceled; completion is not confirmed. The report is retained."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            model.ReportError($"Report export failed: {ex.Message} Choose a new file; the report is retained.");
        }
        catch (Exception ex) { model.ReportError($"Unexpected {ex.GetType().Name}: {ex.Message} Export is not confirmed."); }
        finally { operation = null; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
}
