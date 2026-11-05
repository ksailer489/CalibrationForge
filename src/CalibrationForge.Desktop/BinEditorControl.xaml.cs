using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace CalibrationForge.Desktop;

public partial class BinEditorControl : UserControl
{
    private readonly BinEditorViewModel model;
    private CancellationTokenSource? operation;

    public BinEditorControl(BinEditorViewModel model)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
        model.PropertyChanged += Model_PropertyChanged;
        BuildMatrixColumns();
    }

    private Window Owner => Window.GetWindow(this);

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BinEditorViewModel.Rows)) { BuildMatrixColumns(); }
    }

    private void BuildMatrixColumns()
    {
        MatrixGrid.Columns.Clear();
        if (model.Rows.Count == 0) { return; }
        for (var index = 0; index < model.Rows[0].Cells.Count; index++)
        {
            var content = new FrameworkElementFactory(typeof(ContentControl));
            content.SetBinding(ContentControl.ContentProperty, new Binding($"Cells[{index}]"));
            content.SetValue(ContentControl.ContentTemplateProperty, (DataTemplate)Resources["BinCellTemplate"]);
            var style = new Style(typeof(DataGridCell), TryFindResource(typeof(DataGridCell)) as Style);
            style.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty,
                new Binding($"Cells[{index}].Coordinate")));
            style.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.AutomationIdProperty,
                new Binding($"Cells[{index}].Coordinate")));
            MatrixGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = model.ColumnHeaders[index], Width = 84, MinWidth = 76, CanUserSort = false,
                CanUserReorder = false, CellStyle = style,
                CellTemplate = new DataTemplate { VisualTree = content }
            });
        }
    }

    private void Matrix_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        if (MatrixGrid.SelectedCells.Count == 0) { model.SelectedCell = null; return; }
        var selected = MatrixGrid.SelectedCells[0];
        if (selected.Item is BinMatrixRow row)
        {
            model.SelectedCell = row.Cells[selected.Column.DisplayIndex];
        }
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanLoad) { return; }
        var dialog = new OpenFileDialog { Title = "Inspect BIN offline", Filter = "BIN images (*.bin)|*.bin|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(Owner) != true) { return; }
        if (model.HasUnsavedChanges && MessageBox.Show(Owner, "Discard unsaved BIN edits? Failed loads retain the current image.",
            "Unsaved BIN edits", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { return; }
        await RunAsync(token => model.LoadFileAsync(dialog.FileName, discardUnsaved: true, cancellationToken: token));
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => RunEdit(model.Apply);
    private void RevertCell_Click(object sender, RoutedEventArgs e) => RunEdit(model.RevertCell);
    private void RevertAll_Click(object sender, RoutedEventArgs e) => RunEdit(model.RevertAll);
    private void Undo_Click(object sender, RoutedEventArgs e) => RunEdit(model.Undo);
    private void Redo_Click(object sender, RoutedEventArgs e) => RunEdit(model.Redo);
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase) { return; }
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None && MatrixGrid.IsKeyboardFocusWithin && model.CanEdit)
        {
            ValueInput.Focus();
            ValueInput.SelectAll();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control) { return; }
        if (e.Key == Key.Z) { RunEdit(model.Undo); e.Handled = true; }
        else if (e.Key == Key.Y) { RunEdit(model.Redo); e.Handled = true; }
    }

    private void ValueInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) { return; }
        if (e.Key == Key.Enter)
        {
            if (model.CanApply) { RunEdit(model.Apply); ValueInput.SelectAll(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { model.RestoreInput(); ValueInput.SelectAll(); e.Handled = true; }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanExport) { return; }
        var review = new ExportReviewWindow(model) { Owner = Owner };
        if (review.ShowDialog() != true) { return; }
        var dialog = new SaveFileDialog
        {
            Title = "Choose a NEW filename — experimental BIN, NOT flash-ready",
            Filter = "BIN images (*.bin)|*.bin", DefaultExt = ".bin", AddExtension = true,
            FileName = model.DefaultExportName, OverwritePrompt = false
        };
        if (dialog.ShowDialog(Owner) != true) { return; }
        if (File.Exists(dialog.FileName))
        {
            model.ReportError("Choose a new filename. Existing files, including the loaded original, cannot be overwritten.");
            return;
        }
        await RunAsync(token => model.ExportFileAsync(dialog.FileName, token));
    }

    private void RunEdit(Action action)
    {
        try { action(); }
        catch (ArgumentException ex) { model.ReportError($"Edit rejected: {ex.Message} Previous bytes retained."); }
        catch (InvalidOperationException ex) { model.ReportError(ex.Message); }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null) { return; }
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            model.ReportError("Operation canceled. Current image and edits retained.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            model.ReportError($"Operation failed: {ex.Message} Current image and original retained.");
        }
        finally { operation = null; }
    }
}
