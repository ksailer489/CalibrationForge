using System.Security.Cryptography;
using System.Buffers.Binary;

namespace CalibrationForge.Core;

public sealed class BinImageEditor
{
    private readonly byte[] original;
    private readonly byte[] image;
    private byte[] baseline;
    private int dirtyBytes;
    private int originalDifferenceBytes;
    private readonly List<CellChange[]> history = [];
    private int historyPosition;
    private int historyChanges;
    public const int MaximumImageSize = 16 * 1024 * 1024;
    public const int MaximumHistoryEntries = 256;
    private const int MaximumHistoryChanges = 65536;
    private readonly record struct CellChange(int Offset, ushort Before, ushort After);

    public BinImageEditor(ReadOnlySpan<byte> candidate) : this(candidate, ExperimentalBinProfile.E92Sample) { }

    internal BinImageEditor(ReadOnlySpan<byte> candidate, ExperimentalBinProfile profile)
    {
        ValidateSize(candidate.Length);
        original = candidate.ToArray();
        image = (byte[])original.Clone();
        baseline = (byte[])original.Clone();
        OriginalSha256 = Convert.ToHexString(SHA256.HashData(original));
        Profile = profile.Matches(original) ? profile : null;
    }

    public int Size => original.Length;
    public string OriginalSha256 { get; }
    public ExperimentalBinProfile? Profile { get; }
    public bool AdditionalTableEditsEnabled { get; set; }
    public bool IsDirty => dirtyBytes != 0;
    public bool DiffersFromOriginal => originalDifferenceBytes != 0;
    public bool CanUndo => historyPosition > 0;
    public bool CanRedo => historyPosition < history.Count;
    public decimal Read(int row, int column) => Read(RequireTable(), row, column);
    public decimal ReadOriginal(int row, int column) => ReadOriginal(RequireTable(), row, column);
    public decimal Read(Unsigned16Table table, int row, int column) => RequireTable(table).Read(image, row, column);
    public decimal ReadOriginal(Unsigned16Table table, int row, int column) => RequireTable(table).Read(original, row, column);
    public void Edit(int row, int column, decimal value) => Edit(RequireTable(), row, column, value);

    public void Edit(Unsigned16Table table, int row, int column, decimal value)
    {
        RequireTable(table);
        if (table != Profile!.Table && !AdditionalTableEditsEnabled)
        {
            throw new InvalidOperationException("Additional experimental table editing is disabled. Explicitly enable it before editing.");
        }
        var offset = table.CellOffset(row, column);
        var encoded = table.Encode(value);
        var before = ReadRaw(offset);
        if (before != encoded) { Apply([new CellChange(offset, before, encoded)]); }
    }

    public void RevertCell(int row, int column) => Edit(row, column, ReadOriginal(row, column));
    public void RevertCell(Unsigned16Table table, int row, int column) => Edit(table, row, column, ReadOriginal(table, row, column));

    public void RevertAll()
    {
        if (!DiffersFromOriginal) { return; }
        RequireTable();
        var changes = new List<CellChange>();
        foreach (var table in Profile!.Tables)
        {
            for (var offset = table.Offset; offset < table.Offset + table.ByteLength; offset += 2)
            {
                var before = ReadRaw(offset);
                var after = BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(offset, 2));
                if (before != after) { changes.Add(new CellChange(offset, before, after)); }
            }
        }
        Apply(changes.ToArray());
    }

    public void Undo()
    {
        if (!CanUndo) { return; }
        foreach (var change in history[--historyPosition]) { WriteRaw(change.Offset, change.Before); }
    }

    public void Redo()
    {
        if (!CanRedo) { return; }
        foreach (var change in history[historyPosition++]) { WriteRaw(change.Offset, change.After); }
    }

    public byte[] CopyOriginal() => (byte[])original.Clone();

    public byte[] ExportImage()
    {
        for (var i = 0; i < image.Length; i++)
        {
            if (image[i] != original[i] && (Profile is null || !Profile.Tables.Any(table => i >= table.Offset && i < table.Offset + table.ByteLength)))
            {
                throw new InvalidOperationException("Image contains a change outside the allowed table cells.");
            }
        }

        return (byte[])image.Clone();
    }

    public void MarkSaved(ReadOnlySpan<byte> snapshot)
    {
        if (!image.AsSpan().SequenceEqual(snapshot)) { throw new InvalidOperationException("Image changed during export. Edits remain unsaved."); }
        baseline = snapshot.ToArray();
        dirtyBytes = 0;
    }

    internal static void ValidateSize(long size)
    {
        if (size <= 0 || size > MaximumImageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), $"BIN size must be 1–{MaximumImageSize:N0} bytes.");
        }
    }

    private ushort ReadRaw(int offset) => BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(offset, 2));

    private void WriteRaw(int offset, ushort value)
    {
        SetByte(offset, (byte)(value >> 8));
        SetByte(offset + 1, (byte)value);
    }

    private void SetByte(int offset, byte value)
    {
        dirtyBytes += (value != baseline[offset] ? 1 : 0) - (image[offset] != baseline[offset] ? 1 : 0);
        originalDifferenceBytes += (value != original[offset] ? 1 : 0) - (image[offset] != original[offset] ? 1 : 0);
        image[offset] = value;
    }

    private void Apply(CellChange[] changes)
    {
        for (var index = history.Count - 1; index >= historyPosition; index--)
        {
            historyChanges -= history[index].Length;
            history.RemoveAt(index);
        }
        foreach (var change in changes) { WriteRaw(change.Offset, change.After); }
        history.Add(changes);
        historyChanges += changes.Length;
        // Keep a large revert atomic, but bound retained operations and accumulated cell deltas.
        while (history.Count > 1 && (history.Count > MaximumHistoryEntries || historyChanges > MaximumHistoryChanges))
        {
            historyChanges -= history[0].Length;
            history.RemoveAt(0);
        }
        historyPosition = history.Count;
    }

    private Unsigned16Table RequireTable() => Profile?.Table ?? throw new InvalidOperationException("Unknown BIN is read-only; no verified sample mapping.");

    private Unsigned16Table RequireTable(Unsigned16Table table)
    {
        ArgumentNullException.ThrowIfNull(table);
        RequireTable();
        if (!Profile!.Tables.Contains(table)) { throw new ArgumentException("Table is not part of the recognized profile.", nameof(table)); }
        return table;
    }
}
