using System.Windows;

namespace CalibrationForge.Desktop;

public partial class ExportReviewWindow : Window
{
    public ExportReviewWindow(BinEditorViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        InitializeComponent();
        DataContext = model;
        RowAxisColumn.Header = model.AvailableTables.Count > 1 ? "Row / axis" : model.RowAxisCaption;
        ColumnAxisColumn.Header = model.AvailableTables.Count > 1 ? "Column / axis" : model.ColumnAxisCaption;
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
