using System.Buffers.Binary;
using System.Security.Cryptography;
using CalibrationForge.Core;
using CalibrationForge.Desktop;

namespace CalibrationForge.Core.Tests;

public class BinEditorTests
{
    private static readonly Unsigned16Table Table = new("Independent fixture", 6, 2, 3, 1, 128, "experimental ms");

    private static byte[] Fixture()
    {
        var bytes = Enumerable.Repeat((byte)0xA5, 20).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 2);
        ushort[] raw = [0, 494, 65535, 1, 2, 3];
        for (var i = 0; i < raw.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6 + i * 2), raw[i]);
        }

        return bytes;
    }

    private static ExperimentalBinProfile Profile(byte[] bytes) =>
        new("Independent fixture", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), Table);

    private static readonly Unsigned16Table FuelTable = new("Synthetic fuel reference", 22, 3, 1, 1, 2048, "AFR",
        headerKind: TableHeaderKind.Count);

    private static byte[] MultiTableFixture()
    {
        var bytes = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        Fixture().CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), 3);
        ushort[] values = [28894, 24576, 18432];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22 + index * 2), values[index]);
        }
        return bytes;
    }

    private static ExperimentalBinProfile MultiTableProfile(byte[] bytes) =>
        new("Independent multi-table fixture", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), Table,
            additionalTables: [FuelTable]);

    [Fact]
    public void AdditionalTableRequiresOptInAndCannotBeReplacedByAnArbitraryDefinition()
    {
        var bytes = MultiTableFixture();
        var editor = new BinImageEditor(bytes, MultiTableProfile(bytes));
        Assert.Equal(14.1083984375m, editor.Read(FuelTable, 0, 0));
        Assert.Equal(12m, editor.Read(FuelTable, 1, 0));
        Assert.False(editor.AdditionalTableEditsEnabled);
        Assert.Throws<InvalidOperationException>(() => editor.Edit(FuelTable, 0, 0, 14));
        Assert.False(editor.CanUndo);
        Assert.Equal(bytes, editor.ExportImage());
        editor.AdditionalTableEditsEnabled = true;
        editor.Edit(FuelTable, 0, 0, 14.000244140625m);
        var changed = editor.ExportImage();
        Assert.Equal((byte)0x70, changed[22]);
        Assert.Equal((byte)0x01, changed[23]);
        Assert.Equal(14.00048828125m, editor.Read(FuelTable, 0, 0));
        for (var index = 0; index < bytes.Length; index++)
        {
            if (index is not 22 and not 23) { Assert.Equal(bytes[index], changed[index]); }
        }
        var arbitrary = new Unsigned16Table("Not registered", FuelTable.Offset, 3, 1, 1, 2048, "AFR");
        Assert.Throws<ArgumentException>(() => editor.Read(arbitrary, 0, 0));
        Assert.Throws<ArgumentException>(() => editor.Edit(arbitrary, 0, 0, 12));
        Assert.Equal(3.859375m, editor.Read(0, 1));
        Assert.Equal(bytes, editor.CopyOriginal());
    }

    [Fact]
    public void ProfilesRequireEveryHeaderAndRejectOverlappingDefinitions()
    {
        var bytes = MultiTableFixture();
        bytes[21] = 4;
        Assert.Null(new BinImageEditor(bytes, MultiTableProfile(bytes)).Profile);
        bytes = MultiTableFixture()[..26];
        Assert.Null(new BinImageEditor(bytes, MultiTableProfile(bytes)).Profile);
        var overlap = new Unsigned16Table("Overlapping header", 18, 1, 1, 1, 1, "raw", headerKind: TableHeaderKind.Count);
        Assert.Throws<ArgumentException>(() => new ExperimentalBinProfile("Overlap", 32, "", Table, additionalTables: [overlap]));
        Assert.Throws<ArgumentException>(() => new Unsigned16Table("Invalid count", 22, 2, 2, 1, 1, "raw",
            headerKind: TableHeaderKind.Count));
    }

    [Fact]
    public void MultiTableRevertAndHistoryAreAtomicAndTrackTheSavedBaseline()
    {
        var bytes = MultiTableFixture();
        var profile = MultiTableProfile(bytes);
        var editor = new BinImageEditor(bytes, profile) { AdditionalTableEditsEnabled = true };
        editor.Edit(0, 1, 4);
        editor.Edit(FuelTable, 2, 0, 10);
        var changed = editor.ExportImage();
        editor.MarkSaved(changed);
        editor.AdditionalTableEditsEnabled = false;
        Assert.Throws<InvalidOperationException>(() => editor.Edit(FuelTable, 2, 0, 11));
        editor.RevertAll();
        Assert.Equal(bytes, editor.ExportImage());
        Assert.True(editor.IsDirty);
        editor.Undo();
        Assert.Equal(changed, editor.ExportImage());
        Assert.False(editor.IsDirty);
        editor.Undo();
        Assert.Equal(9m, editor.Read(FuelTable, 2, 0));
        Assert.Equal(4m, editor.Read(0, 1));
        editor.Redo();
        Assert.False(editor.IsDirty);
        editor.Redo();
        Assert.Equal(bytes, editor.ExportImage());
        Assert.Null(new BinImageEditor(changed, profile).Profile);
    }

    [Fact]
    public void TableSelectionKeepsHiddenChangesAndHistoryAndClearsStaleCellSelection()
    {
        var bytes = MultiTableFixture();
        var model = new BinEditorViewModel(new BinWorkspace(MultiTableProfile(bytes)));
        model.Load("multi.bin", bytes, false);
        model.SelectedCell = model.Cells[1];
        var dwellCell = model.SelectedCell;
        model.ValueText = "4";
        model.Apply();
        model.SelectedTable = FuelTable;
        Assert.Null(model.SelectedCell);
        Assert.Equal("", model.ValueText);
        Assert.Equal(3, model.Rows.Count);
        Assert.Single(model.ColumnHeaders);
        model.SelectedCell = model.Cells[2];
        Assert.False(model.CanEdit);
        model.AdditionalTableEditsEnabled = true;
        model.ValueText = "32";
        Assert.False(model.CanApply);
        Assert.Throws<ArgumentOutOfRangeException>(model.Apply);
        model.ValueText = "10";
        model.Apply();
        Assert.Equal(2, model.ChangeCount);
        Assert.Equal(new[] { Table.Name, FuelTable.Name }, model.ChangedCells.Select(cell => cell.TableName));
        model.SetBusy(true);
        Assert.Throws<InvalidOperationException>(() => model.SelectedTable = Table);
        model.SetBusy(false);
        model.SelectedTable = Table;
        Assert.Throws<ArgumentException>(() => model.SelectedCell = model.ChangedCells.Single(cell => cell.TableName == FuelTable.Name));
        Assert.Same(dwellCell, model.Cells[1]);
        model.Undo();
        Assert.Same(dwellCell, Assert.Single(model.ChangedCells));
        model.Redo();
        Assert.Equal(2, model.ChangeCount);
        model.RevertAll();
        Assert.Empty(model.ChangedCells);
        Assert.False(model.HasUnsavedChanges);
        model.Undo();
        Assert.Equal(2, model.ChangeCount);
        model.Load("unknown.bin", new byte[8], true);
        Assert.Empty(model.AvailableTables);
        Assert.Null(model.SelectedTable);
        Assert.Empty(model.ChangedCells);
        model.Load("multi.bin", bytes, false);
        Assert.Same(Table, model.SelectedTable);
        Assert.False(model.AdditionalTableEditsEnabled);
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void CodecReadsRowMajorBigEndianAndQuantizesMidpointsAwayFromZero()
    {
        var bytes = Fixture();
        Assert.Equal(3.859375m, Table.Read(bytes, 0, 1));
        Assert.Equal(511.9921875m, Table.Read(bytes, 0, 2));
        Assert.Equal(0.0078125m, Table.Read(bytes, 1, 0));
        Table.Write(bytes, 0, 1, 4m);
        Assert.Equal(new byte[] { 2, 0 }, bytes[8..10]);
        Assert.Equal((ushort)1, Table.Encode(0.00390625m));
        Assert.Equal((ushort)0, Table.Encode(0.00390624m));
        Assert.Equal((ushort)65535, Table.Encode(Table.Maximum));
    }

    [Fact]
    public void CodecRejectsBoundsIndicesAndShortImagesWithoutMutation()
    {
        var bytes = Fixture();
        var original = (byte[])bytes.Clone();
        Assert.Throws<ArgumentOutOfRangeException>(() => Table.Write(bytes, 0, 0, -0.001m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Table.Write(bytes, 0, 0, Table.Maximum + 0.001m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Table.Write(bytes, -1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Table.Write(bytes, 2, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Table.Write(bytes, 0, 3, 1));
        Assert.Throws<ArgumentException>(() => Table.Write(new byte[17], 0, 0, 1));
        Assert.Throws<OverflowException>(() => new Unsigned16Table("Overflow", int.MaxValue, 2, 3, 1, 128, "ms"));
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void EditorCopiesInputOriginalExportsAndSaveBaselineAndRevertsAgainstOriginal()
    {
        var bytes = Fixture();
        var original = (byte[])bytes.Clone();
        var editor = new BinImageEditor(bytes, Profile(bytes));
        bytes[8] = 0;
        editor.CopyOriginal()[8] = 0;
        Assert.Equal(3.859375m, editor.Read(0, 1));
        Assert.False(editor.IsDirty);
        editor.Edit(0, 1, 4);
        var snapshot = editor.ExportImage();
        for (var i = 0; i < snapshot.Length; i++)
        {
            if (i is not 8 and not 9) { Assert.Equal(original[i], snapshot[i]); }
        }

        Assert.True(editor.IsDirty);
        editor.MarkSaved(snapshot);
        snapshot[8] = 0;
        Assert.Equal(4m, editor.Read(0, 1));
        Assert.False(editor.IsDirty);
        Assert.True(editor.DiffersFromOriginal);
        editor.RevertCell(0, 1);
        Assert.True(editor.IsDirty);
        Assert.Equal(original, editor.ExportImage());
        editor.Edit(1, 1, 10);
        Assert.Throws<InvalidOperationException>(() => editor.MarkSaved(original));
        editor.RevertAll();
        Assert.Equal(original, editor.ExportImage());
        Assert.Equal(original, editor.CopyOriginal());
    }

    [Fact]
    public void ProfileRequiresExactHashSizeAndValidHeaderAndUnknownCannotEdit()
    {
        var bytes = Fixture();
        var profile = Profile(bytes);
        var corrupted = (byte[])bytes.Clone();
        corrupted[19] ^= 1;
        var unknown = new BinImageEditor(corrupted, profile);
        Assert.Null(unknown.Profile);
        Assert.Throws<InvalidOperationException>(() => unknown.Edit(0, 0, 1));
        Assert.Throws<InvalidOperationException>(() => unknown.Read(0, 0));
        Assert.Equal(corrupted, unknown.ExportImage());
        Assert.Null(new BinImageEditor(bytes[..^1], profile).Profile);
        var badHeader = Fixture();
        badHeader[3] = 2;
        Assert.Null(new BinImageEditor(badHeader, Profile(badHeader)).Profile);
        Assert.Null(new BinImageEditor(bytes).Profile);
    }

    [Fact]
    public void WorkspaceGuardsReplacementAndOriginalExportAndClearsUnknownMapping()
    {
        var bytes = Fixture();
        var workspace = new BinWorkspace(Profile(bytes));
        workspace.Load("original.bin", bytes);
        Assert.Throws<InvalidOperationException>(() => workspace.PrepareExport("new.bin"));
        workspace.Image!.Edit(0, 1, 4);
        Assert.Throws<InvalidOperationException>(() => workspace.PrepareExport("ORIGINAL.bin"));
        Assert.Throws<InvalidOperationException>(() => workspace.Load("unknown.bin", new byte[4]));
        Assert.NotNull(workspace.Image.Profile);
        Assert.Equal(4m, workspace.Image.Read(0, 1));
        Assert.Equal(workspace.Image.ExportImage(), workspace.PrepareExport("new.bin"));
        workspace.Load("unknown.bin", new byte[4], discardUnsaved: true);
        Assert.Null(workspace.Image!.Profile);
        Assert.False(workspace.IsDirty);
        Assert.Throws<InvalidOperationException>(() => workspace.PrepareExport("new.bin"));
    }

    [Fact]
    public void ViewModelPreservesSelectedCellThroughApplyRevertAndClearsItOnUnknownLoad()
    {
        var bytes = Fixture();
        var model = new BinEditorViewModel(new BinWorkspace(Profile(bytes)));
        model.Load("original.bin", bytes, false);
        model.SelectedCell = model.Cells[1];
        var selection = model.SelectedCell;
        model.ValueText = "4";
        model.Apply();
        Assert.Same(selection, model.SelectedCell);
        Assert.Equal(4m, selection!.Current);
        Assert.Equal(3.859375m, selection.Original);
        Assert.True(model.CanExport);
        model.ValueText = "NaN";
        Assert.Throws<ArgumentException>(model.Apply);
        model.ValueText = "3,5";
        Assert.Throws<ArgumentException>(model.Apply);
        model.ValueText = "Infinity";
        Assert.Throws<ArgumentException>(model.Apply);
        model.ValueText = "512";
        Assert.Throws<ArgumentOutOfRangeException>(model.Apply);
        Assert.Equal(4m, selection.Current);
        model.RevertCell();
        Assert.False(model.HasUnsavedChanges);
        Assert.Equal(3.859375m, selection.Current);
        model.Load("unknown.bin", new byte[8], false);
        Assert.Empty(model.Cells);
        Assert.Null(model.SelectedCell);
        Assert.Equal("", model.ValueText);
        Assert.False(model.CanApply);
        Assert.False(model.CanExport);
    }

    [Fact]
    public void MatrixRowsMapNonSquareTableToTheSameEditableCells()
    {
        var bytes = Fixture();
        var model = new BinEditorViewModel(new BinWorkspace(Profile(bytes)));
        model.Load("original.bin", bytes, false);
        Assert.Equal(2, model.Rows.Count);
        Assert.All(model.Rows, row => Assert.Equal(3, row.Cells.Count));
        Assert.Equal("R1", model.Rows[1].Header);
        Assert.Equal(new[] { "C0", "C1", "C2" }, model.ColumnHeaders);
        Assert.Equal("Row index", model.RowAxisCaption);
        Assert.Equal("Column index", model.ColumnAxisCaption);
        Assert.Contains("indices only", model.AxisQualification);
        Assert.DoesNotContain("Coil charge", model.TableMeaning);
        Assert.Same(model.Cells[1], model.Rows[0].Cells[1]);
        Assert.Same(model.Cells[3], model.Rows[1].Cells[0]);
        model.SelectedCell = model.Rows[0].Cells[1];
        Assert.Equal("R0C1", model.SelectedCell.Coordinate);
        Assert.Equal("0x8", model.SelectedCell.Offset);
        model.ValueText = "4";
        model.Apply();
        model.SelectedCell = model.Rows[1].Cells[0];
        model.ValueText = "2";
        model.Apply();
        Assert.Equal(4m, model.Rows[0].Cells[1].Current);
        Assert.Equal(2m, model.Rows[1].Cells[0].Current);
        Assert.Equal(511.9921875m, model.Rows[0].Cells[2].Current);
        Assert.Equal(0.015625m, model.Rows[1].Cells[1].Current);
        Assert.Equal(2, model.ChangeCount);
        var selection = model.SelectedCell;
        model.RevertAll();
        Assert.Same(selection, model.SelectedCell);
        Assert.Empty(model.ChangedCells);
        Assert.False(model.Rows[0].Cells[1].IsChanged);
        Assert.False(model.Rows[1].Cells[0].IsChanged);
        Assert.False(model.HasUnsavedChanges);
    }

    [Fact]
    public void AxisDefinitionsCopyCallerValuesAndValidateDimensionsBeforeMappingEdits()
    {
        decimal[] rowValues = [9.5m, 14m];
        var columnValues = new List<decimal> { 100, 750, 2200 };
        var rowAxis = new TableAxis("Supply voltage", "V", rowValues);
        var columnAxis = new TableAxis("Speed", "RPM", columnValues);
        var table = new Unsigned16Table("Independent axes", 6, 2, 3, 1, 128, "ms", rowAxis, columnAxis);
        rowValues[1] = 99;
        columnValues.Clear();
        Assert.Throws<NotSupportedException>(() => ((IList<decimal>)rowAxis.Values)[0] = 99);
        Assert.Throws<ArgumentException>(() => new Unsigned16Table("Bad rows", 6, 3, 3, 1, 128, "ms", rowAxis, columnAxis));
        Assert.Throws<ArgumentException>(() => new Unsigned16Table("Bad columns", 6, 2, 2, 1, 128, "ms", rowAxis, columnAxis));

        var bytes = Fixture();
        var profile = new ExperimentalBinProfile("Independent axes", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), table);
        var model = new BinEditorViewModel(new BinWorkspace(profile));
        model.Load("original.bin", bytes, false);
        Assert.Equal("14 V", model.Rows[1].Header);
        Assert.Equal(new[] { "100 RPM", "750 RPM", "2200 RPM" }, model.ColumnHeaders);
        model.SelectedCell = model.Rows[1].Cells[2];
        Assert.Equal("Supply voltage: 14 V • Speed: 2200 RPM", model.SelectedCell.Context);
        Assert.Equal("R1C2", model.SelectedCell.Coordinate);
        Assert.Equal("0x10", model.SelectedCell.Offset);
        model.ValueText = "4";
        model.Apply();
        var export = model.PrepareExport("new.bin");
        Assert.Equal((ushort)512, table.ReadRaw(export, 1, 2));
        Assert.Equal(bytes[..16], export[..16]);
        Assert.Equal(bytes[18..], export[18..]);
    }

    [Fact]
    public void SampleAxisMetadataFlowsThroughSelectionAndExportReviewWithoutChangingCoordinates()
    {
        var sampleTable = ExperimentalBinProfile.E92Sample.Table;
        var table = new Unsigned16Table(sampleTable.Name, 6, 2, 3, sampleTable.Numerator, sampleTable.Denominator, sampleTable.Unit,
            new TableAxis(sampleTable.RowAxis!.Name, sampleTable.RowAxis.Unit,
                new[] { sampleTable.RowAxis.Values[12], sampleTable.RowAxis.Values[32] }),
            new TableAxis(sampleTable.ColumnAxis!.Name, sampleTable.ColumnAxis.Unit,
                new[] { sampleTable.ColumnAxis.Values[0], sampleTable.ColumnAxis.Values[1], sampleTable.ColumnAxis.Values[16] }),
            sampleTable.Description, sampleTable.ValueName, sampleTable.AxisQualification);
        var bytes = Fixture();
        var profile = new ExperimentalBinProfile("Source-derived fixture", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), table);
        var model = new BinEditorViewModel(new BinWorkspace(profile));
        model.Load("original.bin", bytes, false);
        Assert.Equal("Base ignition dwell (coil charge time)", model.TableName);
        Assert.Equal("Coil charge time before a spark; each cell is milliseconds at the indicated voltage and engine speed.", model.TableMeaning);
        Assert.Equal("Ignition voltage (V)", model.RowAxisCaption);
        Assert.Equal("Engine speed (RPM)", model.ColumnAxisCaption);
        Assert.Equal("Coil charge time (ms)", model.ValueCaption);
        Assert.Equal("Community-defined axes; not OEM/hardware validated", model.AxisQualification);
        Assert.Equal(new[] { "12 V", "32 V" }, model.Rows.Select(row => row.Header));
        Assert.Equal(new[] { "0 RPM", "500 RPM", "8000 RPM" }, model.ColumnHeaders);
        model.SelectedCell = model.Rows[0].Cells[1];
        model.ValueText = "4";
        model.Apply();
        var reviewCell = Assert.Single(model.ChangedCells);
        Assert.Same(model.SelectedCell, reviewCell);
        Assert.Equal("12 V", reviewCell.RowLabel);
        Assert.Equal("500 RPM", reviewCell.ColumnLabel);
        Assert.StartsWith("Ignition voltage: 12 V • Engine speed: 500 RPM", model.CurrentValue);
        Assert.Contains("original 3.859375 ms → current 4 ms • delta +0.140625 ms", reviewCell.Comparison);
        Assert.Equal("R0C1", reviewCell.Coordinate);
        Assert.Equal("0x8", reviewCell.Offset);
        var export = model.PrepareExport("new.bin");
        Assert.Equal((ushort)512, Table.ReadRaw(export, 0, 1));
        model.MarkSaved(export);
        Assert.Same(reviewCell, Assert.Single(model.ChangedCells));
        Assert.Equal(3.859375m, reviewCell.Original);
        Assert.False(model.HasUnsavedChanges);
        model.RevertCell();
        Assert.Empty(model.ChangedCells);
        Assert.Equal("12 V", model.SelectedCell.RowLabel);
        Assert.Equal(3.859375m, model.SelectedCell.Current);
    }

    [Fact]
    public void UnknownImageWithSameNameClearsAllPhysicalLabelsAndMeaning()
    {
        var bytes = Fixture();
        var sampleTable = ExperimentalBinProfile.E92Sample.Table;
        var table = new Unsigned16Table(sampleTable.Name, 6, 2, 3, 1, 128, "ms",
            new TableAxis("Ignition voltage", "V", new decimal[] { 12, 32 }),
            new TableAxis("Engine speed", "RPM", new decimal[] { 0, 500, 8000 }),
            sampleTable.Description, sampleTable.ValueName, sampleTable.AxisQualification);
        var profile = new ExperimentalBinProfile("Axes fixture", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), table);
        var model = new BinEditorViewModel(new BinWorkspace(profile));
        model.Load("E92_4096KiB_12691156.bin", bytes, false);
        model.SelectedCell = model.Rows[0].Cells[1];
        model.ValueText = "4";
        model.Apply();
        model.Load("E92_4096KiB_12691156.bin", model.PrepareExport("new.bin"), true);
        Assert.Empty(model.Rows);
        Assert.Empty(model.ColumnHeaders);
        Assert.Empty(model.ChangedCells);
        Assert.Null(model.SelectedCell);
        Assert.Equal("Row index", model.RowAxisCaption);
        Assert.Equal("Column index", model.ColumnAxisCaption);
        Assert.Equal("Cell value", model.ValueCaption);
        Assert.DoesNotContain("Coil charge", model.TableName);
        Assert.DoesNotContain("Coil charge", model.TableMeaning);
        Assert.DoesNotContain("Community-defined", model.AxisQualification);
        Assert.False(model.CanEdit);
        Assert.False(model.CanExport);
    }

    [Fact]
    public void ChangedCellsNotifyAndCompareAgainstOriginalAfterExportAndRevert()
    {
        var bytes = Fixture();
        var model = new BinEditorViewModel(new BinWorkspace(Profile(bytes)));
        model.Load("original.bin", bytes, false);
        var cell = model.Rows[0].Cells[1];
        var notifications = new List<string?>();
        cell.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        model.SelectedCell = cell;
        Assert.False(model.CanRevertCell);
        model.ValueText = "4";
        model.Apply();
        Assert.True(cell.IsChanged);
        Assert.Equal(0.140625m, cell.Delta);
        Assert.Contains(nameof(BinCell.Current), notifications);
        Assert.Contains(nameof(BinCell.IsChanged), notifications);
        Assert.Contains(nameof(BinCell.Delta), notifications);
        Assert.Same(cell, Assert.Single(model.ChangedCells));
        Assert.Contains("delta +0.140625", model.CurrentValue);
        model.MarkSaved(model.PrepareExport("new.bin"));
        Assert.False(model.HasUnsavedChanges);
        Assert.False(model.CanExport);
        Assert.True(model.CanRevertCell);
        Assert.True(cell.IsChanged);
        Assert.Equal(1, model.ChangeCount);
        model.RevertCell();
        Assert.Same(cell, model.SelectedCell);
        Assert.False(cell.IsChanged);
        Assert.Equal(0m, cell.Delta);
        Assert.Empty(model.ChangedCells);
        Assert.True(model.HasUnsavedChanges);
        Assert.True(model.CanExport);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("3,5")]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("512")]
    public void InvalidInputDisablesOnlyApplyAndAllowsRecovery(string input)
    {
        var bytes = Fixture();
        var model = new BinEditorViewModel(new BinWorkspace(Profile(bytes)));
        model.Load("original.bin", bytes, false);
        model.SelectedCell = model.Rows[0].Cells[1];
        model.ValueText = "4";
        model.Apply();
        model.ValueText = input;
        Assert.True(model.CanEdit);
        Assert.False(model.CanApply);
        Assert.True(model.CanRevertCell);
        Assert.NotEmpty(model.ValidationMessage);
        Assert.ThrowsAny<ArgumentException>(model.Apply);
        Assert.Equal(4m, model.SelectedCell.Current);
        model.ValueText = "4.00390625";
        Assert.True(model.CanApply);
        Assert.Empty(model.ValidationMessage);
        var selection = model.SelectedCell;
        model.Apply();
        Assert.Same(selection, model.SelectedCell);
        Assert.Equal(4.0078125m, selection.Current);
        Assert.Equal("4.0078125", model.ValueText);
        model.ValueText = input;
        model.RevertCell();
        Assert.Equal("3.859375", model.ValueText);
        Assert.Empty(model.ValidationMessage);
    }

    [Fact]
    public void BusyGuardsEditsAndUnknownLoadClearsMatrixReviewAndValidation()
    {
        var bytes = Fixture();
        var model = new BinEditorViewModel(new BinWorkspace(Profile(bytes)));
        model.Load("original.bin", bytes, false);
        model.SelectedCell = model.Rows[0].Cells[1];
        model.ValueText = "4";
        model.Apply();
        model.SetBusy(true);
        model.ValueText = "5";
        model.Apply();
        model.RevertCell();
        model.RevertAll();
        Assert.Equal(4m, model.SelectedCell.Current);
        Assert.False(model.CanEdit);
        Assert.False(model.CanApply);
        Assert.False(model.CanExport);
        model.SetBusy(false);
        model.ValueText = "invalid";
        model.Load("unknown.bin", new byte[8], true);
        Assert.Empty(model.Rows);
        Assert.Empty(model.ChangedCells);
        Assert.Equal(0, model.ChangeCount);
        Assert.Null(model.SelectedCell);
        Assert.Equal("", model.ValueText);
        Assert.Equal("", model.ValidationMessage);
        Assert.Contains("read-only", model.Status);
        Assert.False(model.CanEdit);
        Assert.False(model.CanRevertCell);
    }

    [Fact]
    public void HistoryQuantizesBeforeRecordingAndNoOpsOrInvalidEditsPreserveRedo()
    {
        var bytes = Fixture();
        var editor = new BinImageEditor(bytes, Profile(bytes));
        editor.Edit(0, 0, 0.00390624m);
        editor.RevertCell(0, 0);
        editor.RevertAll();
        Assert.False(editor.CanUndo);
        editor.Edit(0, 0, 0.00390625m);
        Assert.Equal(0.0078125m, editor.Read(0, 0));
        editor.Undo();
        Assert.False(editor.IsDirty);
        editor.Edit(0, 0, 0.001m);
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Edit(0, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Edit(2, 0, 1));
        Assert.True(editor.CanRedo);
        editor.Redo();
        Assert.Equal(0.0078125m, editor.Read(0, 0));
        editor.Edit(0, 0, 0.008m);
        editor.Undo();
        Assert.False(editor.CanUndo);
        Assert.False(editor.DiffersFromOriginal);
    }

    [Fact]
    public void EditingAfterUndoTruncatesOnlyTheRedoBranch()
    {
        var bytes = Fixture();
        var editor = new BinImageEditor(bytes, Profile(bytes));
        editor.Edit(0, 1, 4);
        editor.Edit(0, 1, 5);
        editor.Undo();
        editor.Edit(1, 0, 2);
        Assert.False(editor.CanRedo);
        editor.Redo();
        Assert.Equal(4m, editor.Read(0, 1));
        Assert.Equal(2m, editor.Read(1, 0));
        editor.Undo();
        Assert.Equal(0.0078125m, editor.Read(1, 0));
        editor.Undo();
        Assert.False(editor.IsDirty);
        Assert.False(editor.DiffersFromOriginal);
        editor.Undo();
        Assert.Equal(bytes, editor.ExportImage());
    }

    [Fact]
    public void SaveUndoRedoTracksActualBytesRatherThanHistoryPosition()
    {
        var bytes = Fixture();
        var editor = new BinImageEditor(bytes, Profile(bytes));
        editor.Edit(0, 1, 4);
        var saved = editor.ExportImage();
        editor.MarkSaved(saved);
        saved[8] ^= 0xFF;
        editor.Undo();
        Assert.True(editor.IsDirty);
        Assert.False(editor.DiffersFromOriginal);
        editor.Redo();
        Assert.False(editor.IsDirty);
        Assert.True(editor.DiffersFromOriginal);
        editor.Edit(0, 1, 5);
        Assert.True(editor.IsDirty);
        editor.Edit(0, 1, 4);
        Assert.False(editor.IsDirty);
        editor.RevertCell(0, 1);
        Assert.True(editor.IsDirty);
        Assert.False(editor.DiffersFromOriginal);
        editor.Undo();
        Assert.False(editor.IsDirty);
        editor.Redo();
        Assert.True(editor.IsDirty);
        editor.MarkSaved(editor.ExportImage());
        editor.Undo();
        Assert.True(editor.IsDirty);
        editor.Redo();
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void RevertAllIsOneUndoableTransactionAndNoOpDoesNotConsumeHistory()
    {
        var bytes = Fixture();
        var editor = new BinImageEditor(bytes, Profile(bytes));
        editor.Edit(0, 1, 4);
        editor.Edit(1, 2, 8);
        var edited = editor.ExportImage();
        editor.RevertAll();
        Assert.Equal(bytes, editor.ExportImage());
        editor.RevertAll();
        editor.Undo();
        Assert.Equal(edited, editor.ExportImage());
        editor.Redo();
        Assert.Equal(bytes, editor.ExportImage());
        editor.Undo();
        editor.Undo();
        Assert.Equal(4m, editor.Read(0, 1));
        Assert.Equal(Table.Read(bytes, 1, 2), editor.Read(1, 2));
    }

    [Fact]
    public void HistoryEvictsOldOperationsWithoutLosingSavedBaseline()
    {
        var bytes = Fixture();
        var editor = new BinImageEditor(bytes, Profile(bytes));
        editor.Edit(0, 0, 1);
        editor.MarkSaved(editor.ExportImage());
        for (var index = 0; index < BinImageEditor.MaximumHistoryEntries + 20; index++)
        {
            editor.Edit(0, 0, index % 2 == 0 ? 2 : 1);
        }
        Assert.False(editor.IsDirty);
        var undos = 0;
        while (editor.CanUndo) { editor.Undo(); undos++; }
        Assert.Equal(BinImageEditor.MaximumHistoryEntries, undos);
        while (editor.CanRedo) { editor.Redo(); }
        Assert.False(editor.IsDirty);
        Assert.True(editor.DiffersFromOriginal);
    }

    [Fact]
    public void ViewModelHistoryRetainsSelectionAndLabelsAndInputCanBeRestored()
    {
        var bytes = Fixture();
        var model = new BinEditorViewModel(new BinWorkspace(Profile(bytes)));
        model.Load("original.bin", bytes, false);
        var cell = model.Rows[0].Cells[1];
        model.SelectedCell = cell;
        model.ValueText = "4";
        model.Apply();
        model.ValueText = "bad";
        model.RestoreInput();
        Assert.Equal("4", model.ValueText);
        Assert.Empty(model.ValidationMessage);
        model.Undo();
        Assert.Same(cell, model.SelectedCell);
        Assert.Same(cell, model.Rows[0].Cells[1]);
        Assert.Equal("R0", cell.RowLabel);
        Assert.Equal("C1", cell.ColumnLabel);
        Assert.Equal("3.859375", model.ValueText);
        Assert.Empty(model.ChangedCells);
        Assert.False(model.CanUndo);
        Assert.True(model.CanRedo);
        model.SetBusy(true);
        model.Redo();
        Assert.Equal(3.859375m, cell.Current);
        model.SetBusy(false);
        model.Redo();
        Assert.Same(cell, Assert.Single(model.ChangedCells));
        Assert.Equal("4", model.ValueText);
        model.Load("unknown.bin", new byte[8], true);
        Assert.False(model.CanUndo);
        Assert.False(model.CanRedo);
        model.Load("original.bin", bytes, false);
        Assert.False(model.CanUndo);
        Assert.False(model.CanRedo);
    }

    [Fact]
    public async Task CanceledPreparedImportNeverCommitsOrResetsHistory()
    {
        using var files = new EditorFiles();
        var bytes = Fixture();
        var path = files.PathFor("next.bin");
        await File.WriteAllBytesAsync(path, bytes);
        var workspace = new BinWorkspace(Profile(bytes));
        workspace.Load("original.bin", bytes);
        workspace.Image!.Edit(0, 1, 4);
        var previous = workspace.Image;
        var prepared = await workspace.PrepareLoadAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => workspace.CommitLoad(prepared, true, cancellation.Token));
        Assert.Same(previous, workspace.Image);
        Assert.EndsWith("original.bin", workspace.SourcePath);
        Assert.True(previous.CanUndo);
        Assert.True(previous.IsDirty);
        Assert.Throws<InvalidOperationException>(() => workspace.CommitLoad(prepared));
        Assert.Same(previous, workspace.Image);
    }

    [Fact]
    public async Task FileImportsRejectEmptyAndOversizedFilesBeforeReplacingCurrentEdits()
    {
        using var files = new EditorFiles();
        var bytes = Fixture();
        var model = new BinEditorViewModel(new BinWorkspace(Profile(bytes)));
        model.Load("original.bin", bytes, false);
        model.SelectedCell = model.Cells[1];
        var selected = model.SelectedCell;
        model.ValueText = "4";
        model.Apply();
        foreach (var size in new long[] { 0, BinImageEditor.MaximumImageSize + 1L })
        {
            var path = files.PathFor($"{size}.bin");
            using (var stream = File.Create(path)) { stream.SetLength(size); }
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => model.LoadFileAsync(path, true));
            Assert.Same(selected, model.SelectedCell);
            Assert.Equal(4m, selected!.Current);
            Assert.True(model.CanUndo);
            Assert.True(model.HasUnsavedChanges);
            Assert.False(model.IsBusy);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Load("empty.bin", [], true));
    }

    [Fact]
    public async Task CanceledOrFailedFileLoadsPreserveProfileSelectionAndPendingInput()
    {
        using var files = new EditorFiles();
        var bytes = Fixture();
        var model = new BinEditorViewModel(new BinWorkspace(Profile(bytes)));
        model.Load("original.bin", bytes, false);
        model.SelectedCell = model.Cells[1];
        model.ValueText = "4";
        model.Apply();
        model.ValueText = "pending input";
        var selected = model.SelectedCell;
        var rows = model.Rows;
        var source = model.Source;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            model.LoadFileAsync(files.PathFor("absent.bin"), true, cancellation.Token));
        await Assert.ThrowsAsync<FileNotFoundException>(() => model.LoadFileAsync(files.PathFor("absent.bin"), true));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => model.LoadFileAsync("\0", true));
        Assert.Same(selected, model.SelectedCell);
        Assert.Same(rows, model.Rows);
        Assert.Equal(source, model.Source);
        Assert.Equal("pending input", model.ValueText);
        Assert.True(model.HasUnsavedChanges);
        Assert.True(model.CanUndo);
        Assert.True(model.CanEdit);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task FileExportCancellationFailureAndSuccessKeepTheCorrectBaseline()
    {
        using var files = new EditorFiles();
        var bytes = Fixture();
        var originalPath = files.PathFor("original.bin");
        await File.WriteAllBytesAsync(originalPath, bytes);
        var model = new BinEditorViewModel(new BinWorkspace(Profile(bytes)));
        await model.LoadFileAsync(originalPath);
        model.SelectedCell = model.Cells[1];
        model.ValueText = "4";
        model.Apply();
        var exportPath = files.PathFor("export.bin");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.ExportFileAsync(exportPath, cancellation.Token));
        Assert.False(File.Exists(exportPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.ExportFileAsync(originalPath));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => model.ExportFileAsync(files.PathFor("absent\\new.bin")));
        var existingPath = files.PathFor("existing.bin");
        await File.WriteAllBytesAsync(existingPath, bytes);
        await Assert.ThrowsAsync<IOException>(() => model.ExportFileAsync(existingPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(existingPath));
        Assert.True(model.HasUnsavedChanges);
        Assert.True(model.CanUndo);
        await model.ExportFileAsync(exportPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(originalPath));
        Assert.Equal(4m, Table.Read(await File.ReadAllBytesAsync(exportPath), 0, 1));
        Assert.False(model.HasUnsavedChanges);
        Assert.False(model.IsBusy);
        model.Undo();
        Assert.True(model.HasUnsavedChanges);
        model.Redo();
        Assert.False(model.HasUnsavedChanges);
        Assert.Equal(3, Directory.GetFiles(files.DirectoryPath).Length);
    }

    [Fact]
    public async Task SuccessfulUnknownFileImportClearsOldHistoryAndMapping()
    {
        using var files = new EditorFiles();
        var bytes = Fixture();
        var model = new BinEditorViewModel(new BinWorkspace(Profile(bytes)));
        model.Load("original.bin", bytes, false);
        model.SelectedCell = model.Cells[1];
        model.ValueText = "4";
        model.Apply();
        var path = files.PathFor("unknown.bin");
        await File.WriteAllBytesAsync(path, new byte[8]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.LoadFileAsync(path));
        Assert.True(model.CanUndo);
        await model.LoadFileAsync(path, true);
        Assert.Equal(path, model.Source);
        Assert.Null(model.SelectedCell);
        Assert.Empty(model.Cells);
        Assert.Empty(model.ChangedCells);
        Assert.False(model.CanUndo);
        Assert.False(model.CanRedo);
        Assert.False(model.HasUnsavedChanges);
        Assert.False(model.CanEdit);
    }

    private sealed class EditorFiles : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Environment.CurrentDirectory, $"calibrationforge-editor-test-{Guid.NewGuid():N}");
        public EditorFiles() => Directory.CreateDirectory(DirectoryPath);
        public string PathFor(string filename) => Path.Combine(DirectoryPath, filename);
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    [Fact]
    public async Task AtomicExportNeverReplacesAnExistingFileAndLeavesNoStagingFiles()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, $"calibrationforge-bin-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "export.bin");
        try
        {
            var bytes = Fixture();
            await AtomicImageFile.SaveAsync(path, bytes);
            var changed = (byte[])bytes.Clone();
            changed[8] = 2;
            await Assert.ThrowsAsync<IOException>(() => AtomicImageFile.SaveAsync(path, changed));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AtomicImageFile.SaveAsync(Path.Combine(directory, "canceled.bin"), changed, cancellationToken: cancellation.Token));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
