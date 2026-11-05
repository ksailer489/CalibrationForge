using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CalibrationForge.Core;
using CalibrationForge.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var watchdog = new Timer(_ => Environment.Exit(2), null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var bindingErrors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        var application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var directory = Path.Combine(AppContext.BaseDirectory, "smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        try
        {
            var table = new Unsigned16Table("Synthetic smoke matrix — not a vehicle calibration", 4, 33, 17, 1, 128, "demo");
            var fuel = new Unsigned16Table("Synthetic fuel reference", table.Offset + table.ByteLength + 2, 3, 1, 1, 2048, "AFR",
                headerKind: TableHeaderKind.Count);
            var bytes = new byte[fuel.Offset + fuel.ByteLength];
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0), 17);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 33);
            for (var index = 0; index < 33 * 17; index++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4 + index * 2), 128);
            }
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(fuel.Offset - 2), 3);
            for (var index = 0; index < 3; index++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(fuel.Offset + index * 2), (ushort)((14 - index) * 2048));
            }
            var profile = new ExperimentalBinProfile("Independent synthetic smoke fixture", bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)), table, additionalTables: [fuel]);
            window = new MainWindow(new BinWorkspace(profile))
            {
                ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000, Top = -30000, Width = 1060, Height = 900
            };
            window.Show();
            Pump(window);
            var tabs = Descendants(window).OfType<TabControl>().Single();
            Check(Id(tabs.SelectedItem as DependencyObject) == "BinEditorTab", "BIN editor selected at startup");
            Check(Find<Button>(window, "BinLoadButton").IsVisible, "default BIN loader visible");
            Check(Find<TextBlock>(window, "OfflineStatus").IsVisible, "persistent offline status visible");
            var editor = (BinEditorControl)((ContentControl)window.FindName("BinEditorHost")).Content;
            var bin = (BinEditorViewModel)editor.DataContext;
            var matrix = Find<DataGrid>(window, "BinCellsGrid");
            Check(matrix.Items.Count == 0 && !Find<Button>(window, "BinExportButton").IsEnabled, "unknown BIN empty and read-only");
            var source = Path.Combine(directory, "synthetic.bin");
            File.WriteAllBytes(source, bytes);
            Wait(bin.LoadFileAsync(source), window);
            Check(matrix.Items.Count == 33 && matrix.Columns.Count == 17, "synthetic grid loaded asynchronously on UI thread");
            matrix.ScrollIntoView(matrix.Items[12], matrix.Columns[0]);
            Pump(window);
            matrix.SelectedCells.Add(new DataGridCellInfo(matrix.Items[12], matrix.Columns[0]));
            matrix.CurrentCell = new DataGridCellInfo(matrix.Items[12], matrix.Columns[0]);
            Pump(window);
            Check(bin.SelectedCell is { Row: 12, Column: 0 }, "actual grid selection maps to model");
            var input = Find<TextBox>(window, "BinValueInput");
            input.Text = "-1";
            input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Pump(window);
            Check(input.IsEnabled && !Find<Button>(window, "BinApplyButton").IsEnabled, "invalid input remains recoverable");
            input.Text = "2";
            input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Pump(window);
            Click(window, "BinApplyButton");
            Check(bin.SelectedCell!.Current == 2 && bin.CanUndo, "apply updates selected cell");
            Click(window, "BinUndo");
            Check(bin.SelectedCell.Current == 1 && bin.CanRedo, "undo actual button binding");
            Click(window, "BinRedo");
            Check(bin.SelectedCell.Current == 2 && bin.CanUndo, "redo actual button binding");
            var tableSelector = Find<ComboBox>(window, "BinTableSelector");
            Check(tableSelector.Items.Count == 2 && tableSelector.IsEnabled, "multiple table selector populated");
            tableSelector.SelectedItem = fuel;
            Pump(window);
            Check(matrix.Items.Count == 3 && matrix.Columns.Count == 1 && bin.SelectedCell is null, "table selection rebuilds matrix and clears stale cell");
            matrix.SelectedCells.Add(new DataGridCellInfo(matrix.Items[1], matrix.Columns[0]));
            Pump(window);
            var additionalEdits = Find<CheckBox>(window, "BinAdditionalTableEdits");
            Check(additionalEdits.IsChecked == false && !input.IsEnabled, "additional table editing is opt-in");
            additionalEdits.IsChecked = true;
            Pump(window);
            input.Text = "12";
            input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Click(window, "BinApplyButton");
            Check(bin.ChangeCount == 2 && bin.SelectedCell!.Current == 12, "second table edit preserves hidden first-table edit");
            additionalEdits.IsChecked = false;
            Pump(window);
            Check(!input.IsEnabled && bin.ChangeCount == 2 && bin.CanExport, "disabling additional edits preserves reviewable changes");
            tableSelector.SelectedItem = table;
            Pump(window);
            Click(window, "BinUndo");
            Check(bin.ChangeCount == 1, "global undo updates hidden table");
            Click(window, "BinRedo");
            Check(bin.ChangeCount == 2, "global redo updates hidden table");
            Check(IsDark(matrix.Background) && IsDark(matrix.RowBackground), "shared dark matrix theme");
            CaptureSizes(window, args, "bin", () =>
            {
                matrix.ScrollIntoView(matrix.Items[12], matrix.Columns[0]);
                Pump(window);
                Check(((Grid)VisualTreeHelper.GetParent(matrix)).RowDefinitions[1].ActualHeight >= 100, "minimum visible BIN grid row");
            });
            var review = new ExportReviewWindow(bin)
            {
                ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000, Top = -30000
            };
            review.Show();
            Pump(review);
            Check(Find<DataGrid>(review, "BinExportReviewGrid").Items.Count == 2, "export comparison includes hidden table changes");
            Check(IsDark(Find<DataGrid>(review, "BinExportReviewGrid").RowBackground), "export review uses dark theme");
            review.Close();
            Wait(bin.ExportFileAsync(Path.Combine(directory, "edited.bin")), window);
            Check(File.ReadAllBytes(source).SequenceEqual(bytes) && !bin.HasUnsavedChanges, "export preserves source and saved baseline");
            bin.RevertAll();
            bin.Load(source, bytes, true);
            tableSelector.SelectedItem = fuel;
            Pump(window);
            bin.Load(Path.Combine(directory, "unknown.bin"), new byte[8], false);
            Pump(window);
            Check(tableSelector.Items.Count == 0 && bin.SelectedTable is null && matrix.Items.Count == 0,
                "unknown load clears table selector, selection and matrix");
            bin.Load(source, bytes, false);
            Pump(window);
            Check(matrix.Columns.Count == 17 && additionalEdits.IsChecked == false,
                "recognized reload restores primary table with additional editing disabled");

            SelectTab(tabs, "DiagnosticsTab", window);
            var feature = Find<CheckBox>(window, "DiagnosticsFeatureSwitch");
            Check(feature.IsChecked == false, "diagnostics defaults off");
            feature.IsChecked = true;
            Pump(window);
            Check(!Find<Button>(window, "DiagnosticsExportButton").IsEnabled, "empty diagnostics cannot export");
            Click(window, "DiagnosticsDemoButton");
            var codes = Find<DataGrid>(window, "DiagnosticsCodesGrid");
            Check(codes.Items.Count == 3, "diagnostics example faults rendered");
            Check(Find<TextBlock>(window, "DiagnosticsSourceText").Text.Contains("SIMULATED"), "diagnostics simulation provenance visible");
            Check(IsDark(codes.RowBackground), "diagnostics dark rows");
            var modules = Find<ComboBox>(window, "DiagnosticsModules");
            modules.IsDropDownOpen = true;
            Pump(window);
            Check(modules.IsDropDownOpen, "dark controller selector opens");
            modules.IsDropDownOpen = false;
            CaptureSizes(window, args, "diagnostics", () => Check(codes.ActualHeight >= 100, "minimum diagnostics grid area"));

            SelectTab(tabs, "ToolsTab", window);
            var toolsControl = (ToolsControl)((ContentControl)window.FindName("ToolsHost")).Content;
            var tools = (ToolsViewModel)toolsControl.DataContext;
            Check(Find<TextBox>(window, "ToolsResult").Text.Length == 0, "tools initial empty result");
            Click(window, "ToolsIdentityDemo", wait: false);
            WaitUntil(() => !tools.IsBusy, window);
            Check(Find<TextBlock>(window, "ToolsProvenance").Text.Contains("SIMULATED"), "tools provenance binding");
            Check(Find<TextBox>(window, "ToolsResult").Text.Contains("Reported VIN"), "tools identity result binding");
            Check(!Find<Button>(window, "ToolsCancel").IsEnabled && Find<Button>(window, "ToolsVerifyFile").IsEnabled, "tools completion restores gating");
            CaptureSizes(window, args, "tools", () => Check(Find<TextBox>(window, "ToolsResult").ActualHeight > 80, "minimum tools result area"));
            Wait(tools.VerifyFileAsync(source, Path.Combine(directory, "verified.zip")), window);
            Check(tools.Result.Contains("Unknown bytes in requested range: 0") && File.Exists(Path.Combine(directory, "verified.zip")), "tools file verification ZIP");
            Wait(tools.AnalyzeAsync(source, null), window);
            Check(tools.Provenance.Contains("FAILED") && tools.Result.Length == 0, "unsupported checksum reference cannot show prior success");
            Wait(tools.ExportLogAsync(Path.Combine(directory, "support.json")), window);
            Check(File.Exists(Path.Combine(directory, "support.json")), "support log export");
            SelectTab(tabs, "SimulatorTab", window);
            Click(window, "ReadSimulatorButton", wait: false);
            WaitUntil(() => Find<Button>(window, "ReadSimulatorButton").IsEnabled, window);
            Check(Find<TextBox>(window, "ImageHashText").Text.Length == 64, "existing simulator binding");
            Check(bindingErrors.Errors.Count == 0, "no WPF binding errors: " + string.Join("\n", bindingErrors.Errors));
            Console.WriteLine("Desktop smoke passed: startup, dark theme, editor async load/apply/undo/redo/export, diagnostics, tools/replay/verification/failure/support log, simulator and normal/minimum layout.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (window?.FindName("BinEditorHost") is ContentControl { Content: BinEditorControl editor } &&
                editor.DataContext is BinEditorViewModel { IsBusy: false } bin)
            {
                bin.Load(Path.Combine(directory, "cleanup.bin"), new byte[32], true);
            }
            window?.Close();
            application.Shutdown();
            Directory.Delete(directory, true);
        }
    }

    private static string Id(DependencyObject? element) => element is null ? "" : AutomationProperties.GetAutomationId(element);
    private static bool IsDark(Brush brush) => brush is SolidColorBrush solid && solid.Color.R < 100 && solid.Color.G < 100 && solid.Color.B < 120;
    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) { yield return child; }
        }
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants(root).OfType<T>().FirstOrDefault(element => Id(element) == id)
        ?? throw new InvalidOperationException("Missing control: " + id);

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void WaitUntil(Func<bool> done, Window window)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) { throw new TimeoutException("Desktop smoke operation timed out."); }
            Pump(window);
            Thread.Sleep(5);
        }
        Pump(window);
    }

    private static void Wait(Task operation, Window window)
    {
        WaitUntil(() => operation.IsCompleted, window);
        operation.GetAwaiter().GetResult();
    }

    private static void Click(Window window, string id, bool wait = true)
    {
        var button = Find<Button>(window, id);
        Check(button.IsEnabled, "enabled action: " + id);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (wait) { Pump(window); }
    }

    private static void SelectTab(TabControl tabs, string id, Window window)
    {
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => Id(tab) == id);
        Pump(window);
    }

    private static void CaptureSizes(Window window, string[] args, string name, Action check)
    {
        foreach (var size in new[] { (Width: 1060, Height: 900, Name: "normal"), (Width: 800, Height: 650, Name: "minimum") })
        {
            window.Width = size.Width;
            window.Height = size.Height;
            Pump(window);
            check();
            Check(Find<TextBlock>(window, "OfflineStatus").IsVisible, "offline status retained at " + size.Name);
            if (args.Length != 0)
            {
                Directory.CreateDirectory(args[0]);
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(args[0], $"{name}-{size.Name}.png");
                using var output = File.Create(path);
                encoder.Save(output);
                Console.WriteLine("Screenshot: " + path);
            }
        }
        window.Width = 1060;
        window.Height = 900;
        Pump(window);
    }

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message?.Contains("Error:", StringComparison.Ordinal) == true) { Errors.Add(message); }
        }
    }
}
