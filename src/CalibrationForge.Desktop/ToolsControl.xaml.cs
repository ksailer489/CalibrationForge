using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CalibrationForge.Desktop;

public partial class ToolsControl : UserControl
{
    private readonly ToolsViewModel model;

    public ToolsControl(ToolsViewModel model)
    {
        InitializeComponent();
        DataContext = this.model = model;
    }

    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
        if (model.CanStart) { await model.IdentityAsync(); }
    }

    private async void Replay_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanStart) { return; }
        var path = Open("Replay identity from an ordered CAN trace", "CAN trace (*.json)|*.json");
        if (path is not null) { await model.IdentityAsync(path); }
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanStart) { return; }
        var source = Open("Verify a source file (two independent FILE reads)", "Binary file (*.bin)|*.bin|All files (*.*)|*.*");
        if (source is null) { return; }
        var output = Save("New integrity/provenance ZIP (existing files refused)", "ZIP bundle (*.zip)|*.zip", "verified-file.zip");
        if (output is not null) { await model.VerifyFileAsync(source, output); }
    }

    private async void Checksum_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanStart) { return; }
        var reference = Open("Select exact supported original reference BIN", "BIN image (*.bin)|*.bin");
        if (reference is null) { return; }
        var candidate = Open("Select candidate BIN to analyze (may be the same reference)", "BIN image (*.bin)|*.bin");
        if (candidate is not null) { await model.AnalyzeAsync(reference, candidate); }
    }

    private async void Support_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanStart) { return; }
        var path = Save("Export metadata-only support log (new file)", "JSON log (*.json)|*.json", "calibrationforge-support.json");
        if (path is not null) { await model.ExportLogAsync(path); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => model.Cancel();

    private string? Open(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }

    private string? Save(string title, string filter, string filename)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = filename, AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }
}
