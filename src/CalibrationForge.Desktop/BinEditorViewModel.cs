using System.ComponentModel;
using System.Globalization;
using CalibrationForge.Core;

namespace CalibrationForge.Desktop;

public sealed class BinCell(int row, int column, string offset, decimal original, decimal current, string unit,
    string? rowLabel = null, string? columnLabel = null, string? context = null) : INotifyPropertyChanged
{
    public int Row { get; } = row;
    public int Column { get; } = column;
    public string Offset { get; } = offset;
    public decimal Original { get; } = original;
    public decimal Current { get; private set; } = current;
    public string Coordinate => $"R{Row}C{Column}";
    public string RowLabel { get; } = rowLabel ?? $"R{row}";
    public string ColumnLabel { get; } = columnLabel ?? $"C{column}";
    public string Context { get; } = context ?? $"R{row}C{column}";
    public bool IsChanged => Current != Original;
    public decimal Delta => Current - Original;
    public string Comparison => $"{Context} • original {Original.ToString(CultureInfo.InvariantCulture)} {Unit} → current {Current.ToString(CultureInfo.InvariantCulture)} {Unit} • delta {Delta.ToString("+0.#########;-0.#########;0", CultureInfo.InvariantCulture)} {Unit} • {Coordinate} • {Offset}";
    public string Unit { get; } = unit;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(decimal value)
    {
        if (Current == value) { return; }
        Current = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChanged)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Delta)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Comparison)));
    }
}

public sealed class BinMatrixRow(int index, IReadOnlyList<BinCell> cells, string? header = null)
{
    public string Header { get; } = header ?? $"R{index}";
    public IReadOnlyList<BinCell> Cells { get; } = cells;
}

public sealed class BinEditorViewModel : INotifyPropertyChanged
{
    private readonly BinWorkspace workspace;
    private BinCell? selectedCell;
    private string valueText = "";
    private string status = "Load a BIN for inspection. Only the exact original public sample can be edited.";
    private bool busy;

    public BinEditorViewModel(BinWorkspace workspace)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<BinCell> Cells { get; private set; } = Array.Empty<BinCell>();
    public IReadOnlyList<BinMatrixRow> Rows { get; private set; } = Array.Empty<BinMatrixRow>();
    public IReadOnlyList<string> ColumnHeaders { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<BinCell> ChangedCells { get; private set; } = Array.Empty<BinCell>();
    public int ChangeCount => ChangedCells.Count;
    public string ChangeSummary => $"{ChangeCount} cell(s) differ from the loaded original (not the last export).";
    public BinCell? SelectedCell
    {
        get => selectedCell;
        set
        {
            if (value is not null && !Cells.Contains(value)) { throw new ArgumentException("Select a cell from the loaded matrix.", nameof(value)); }
            selectedCell = value;
            valueText = value?.Current.ToString(CultureInfo.InvariantCulture) ?? "";
            Refresh();
        }
    }

    public string ValueText
    {
        get => valueText;
        set { valueText = value; Refresh(); }
    }

    public string Status => status;
    public bool IsBusy => busy;
    public bool CanLoad => !busy;
    public bool CanEdit => !busy && SelectedCell is not null && workspace.Image?.Profile is not null;
    public bool CanApply => CanEdit && ValidationMessage.Length == 0;
    public bool CanRevertCell => CanEdit && SelectedCell!.IsChanged;
    public bool CanUndo => !busy && workspace.Image?.CanUndo == true;
    public bool CanRedo => !busy && workspace.Image?.CanRedo == true;
    public string ValidationMessage
    {
        get
        {
            if (SelectedCell is null || workspace.Image?.Profile is null) { return ""; }
            try { _ = ParseValue(); return ""; }
            catch (ArgumentException ex) { return ex.Message.Split(Environment.NewLine)[0]; }
        }
    }
    public bool CanExport => !busy && workspace.Image?.Profile is not null && workspace.IsDirty;
    public bool CanRevert => !busy && workspace.Image?.DiffersFromOriginal == true;
    public bool HasUnsavedChanges => workspace.IsDirty;
    public string Source => workspace.SourcePath ?? "No BIN loaded";
    public string DefaultExportName => workspace.SourcePath is null ? "image.experimental.bin" :
        System.IO.Path.GetFileNameWithoutExtension(workspace.SourcePath) + ".experimental.bin";
    public string Metadata => workspace.Image is { } image ? $"{image.Size:N0} bytes • Original SHA256: {image.OriginalSha256}" : "Size and SHA256 appear after loading.";
    public string ProfileStatus => workspace.Image?.Profile is { } profile
        ? $"{profile.Name} • exact original hash, header, size and segment metadata matched. Not GM-verified."
        : "Read-only: no exact original sample profile match. Edited exports reopen as unknown/read-only.";
    public string TableDescription => workspace.Image?.Profile?.Table is { } table
        ? $"{table.Name} • {table.Rows} rows × {table.Columns} columns • BE unsigned 16-bit × {table.Numerator} / {table.Denominator} • {table.Unit} • data 0x{table.Offset:X}. Row-major."
        : "No labeled calibration table available.";
    public string TableName => workspace.Image?.Profile?.Table.Name ?? "No labeled calibration table available";
    public string TableMeaning => workspace.Image?.Profile?.Table is { } table
        ? table.Description.Length > 0 ? table.Description : "Cell values at the indicated row and column indices."
        : "Load an exact supported sample to see definition-provided labels.";
    public string RowAxisCaption => workspace.Image?.Profile?.Table.RowAxis?.Caption ?? "Row index";
    public string ColumnAxisCaption => workspace.Image?.Profile?.Table.ColumnAxis?.Caption ?? "Column index";
    public string ValueCaption => workspace.Image?.Profile?.Table is { } table ? $"{table.ValueName} ({table.Unit})" : "Cell value";
    public string AxisQualification => workspace.Image?.Profile?.Table is { } table
        ? table.AxisQualification.Length > 0 ? table.AxisQualification :
            table.RowAxis is null && table.ColumnAxis is null ? "Unlabeled axes; row and column indices only." : "Definition-provided axes; not OEM/hardware validated"
        : "No axis mapping available for this image.";
    public string Encoding => workspace.Image?.Profile?.Table is { } table
        ? $"Encoding bounds 0–{table.Maximum.ToString(CultureInfo.InvariantCulture)} {table.Unit}, NOT safe tuning limits. Nearest raw integer; midpoints away from zero."
        : "";
    public string CurrentValue => SelectedCell is { } cell
        ? cell.Comparison
        : "Select a table cell.";
    public string DirtyState => workspace.Image is null ? "No BIN loaded" :
        workspace.IsDirty ? "Unsaved BIN edits" : workspace.Image.DiffersFromOriginal ? "Exported edits; no pending changes" : "Unchanged original";
    public string Attribution => ExperimentalBinProfile.Attribution;

    public void Load(string path, ReadOnlySpan<byte> bytes, bool discardUnsaved)
    {
        if (busy) { throw new InvalidOperationException("An editor file operation is already running."); }
        workspace.Load(path, bytes, discardUnsaved);
        CompleteLoad();
    }

    public async Task LoadFileAsync(string path, bool discardUnsaved = false, CancellationToken cancellationToken = default)
    {
        BeginOperation();
        try
        {
            var prepared = await workspace.PrepareLoadAsync(path, cancellationToken);
            workspace.CommitLoad(prepared, discardUnsaved, cancellationToken);
            CompleteLoad();
        }
        finally { SetBusy(false); }
    }

    public async Task ExportFileAsync(string path, CancellationToken cancellationToken = default)
    {
        BeginOperation();
        try
        {
            await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var editor = workspace.Image;
                var snapshot = workspace.PrepareExport(path);
                cancellationToken.ThrowIfCancellationRequested();
                await AtomicImageFile.SaveAsync(path, snapshot, cancellationToken: cancellationToken).ConfigureAwait(false);
                editor!.MarkSaved(snapshot);
            }, cancellationToken);
            status = "Experimental full-size BIN exported. Checksums NOT updated. NOT flash-ready. Original preserved. Export reopens read-only.";
        }
        finally { SetBusy(false); }
    }

    private void CompleteLoad()
    {
        selectedCell = null;
        valueText = "";
        RebuildCells();
        status = workspace.Image!.Profile is null ? "BIN inspected; unknown sample is read-only. Previous mapping and selection cleared." :
            "Original public E92 sample recognized. Vehicle provenance unknown; not a verified 2017 Camaro calibration.";
        Refresh();
    }

    public void Apply()
    {
        if (!CanEdit) { return; }
        var value = ParseValue();
        workspace.Image!.Edit(selectedCell!.Row, selectedCell.Column, value);
        UpdateCells();
        status = "Cell applied with raw-value quantization. Checksums unchanged; no claim of mechanical safety.";
        Refresh();
    }

    public void RevertCell()
    {
        if (!CanRevertCell) { return; }
        workspace.Image!.RevertCell(selectedCell!.Row, selectedCell.Column);
        UpdateCells();
        status = "Selected cell restored to the loaded original.";
        Refresh();
    }

    public void RevertAll()
    {
        if (!CanRevert) { return; }
        workspace.Image?.RevertAll();
        UpdateCells();
        status = "All cells restored to the loaded original.";
        Refresh();
    }

    public void Undo()
    {
        if (!CanUndo) { return; }
        workspace.Image!.Undo();
        UpdateCells();
        status = "BIN edit undone. Checksums unchanged; NOT flash-ready.";
        Refresh();
    }

    public void Redo()
    {
        if (!CanRedo) { return; }
        workspace.Image!.Redo();
        UpdateCells();
        status = "BIN edit redone. Checksums unchanged; NOT flash-ready.";
        Refresh();
    }

    public void RestoreInput()
    {
        valueText = selectedCell?.Current.ToString(CultureInfo.InvariantCulture) ?? "";
        Refresh();
    }

    public byte[] PrepareExport(string path) => workspace.PrepareExport(path);
    public void MarkSaved(byte[] snapshot)
    {
        workspace.Image!.MarkSaved(snapshot);
        status = "Experimental full-size BIN exported. Checksums NOT updated. NOT flash-ready. Original preserved. Export reopens read-only.";
        Refresh();
    }

    public void SetBusy(bool value) { busy = value; Refresh(); }
    public void ReportError(string message) { status = message; Refresh(); }

    private void BeginOperation()
    {
        if (busy) { throw new InvalidOperationException("An editor file operation is already running."); }
        SetBusy(true);
    }

    private void RebuildCells()
    {
        var cells = new List<BinCell>();
        var rows = new List<BinMatrixRow>();
        ColumnHeaders = Array.Empty<string>();
        if (workspace.Image?.Profile?.Table is { } table)
        {
            ColumnHeaders = Array.AsReadOnly(Enumerable.Range(0, table.Columns)
                .Select(column => table.ColumnAxis?.Label(column) ?? $"C{column}").ToArray());
            for (var row = 0; row < table.Rows; row++)
            {
                var rowCells = new List<BinCell>();
                var rowLabel = table.RowAxis?.Label(row) ?? $"R{row}";
                for (var column = 0; column < table.Columns; column++)
                {
                    var columnLabel = ColumnHeaders[column];
                    var context = $"{table.RowAxis?.Name ?? "Row index"}: {rowLabel} • {table.ColumnAxis?.Name ?? "Column index"}: {columnLabel}";
                    var cell = new BinCell(row, column, $"0x{table.CellOffset(row, column):X}",
                        workspace.Image.ReadOriginal(row, column), workspace.Image.Read(row, column), table.Unit,
                        rowLabel, columnLabel, context);
                    cells.Add(cell);
                    rowCells.Add(cell);
                }
                rows.Add(new BinMatrixRow(row, rowCells.AsReadOnly(), rowLabel));
            }
        }

        Cells = cells.AsReadOnly();
        Rows = rows.AsReadOnly();
        ChangedCells = Array.Empty<BinCell>();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rows)));
    }

    private void UpdateCells()
    {
        foreach (var cell in Cells)
        {
            cell.Update(workspace.Image!.Read(cell.Row, cell.Column));
        }

        valueText = selectedCell?.Current.ToString(CultureInfo.InvariantCulture) ?? "";
        ChangedCells = Cells.Where(cell => cell.IsChanged).ToArray();
    }

    private decimal ParseValue()
    {
        if (!decimal.TryParse(valueText, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint |
            NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException("Enter a finite decimal using '.' as the separator.");
        }
        _ = workspace.Image!.Profile!.Table.Encode(value);
        return value;
    }

    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
