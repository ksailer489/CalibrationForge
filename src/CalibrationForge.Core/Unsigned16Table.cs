using System.Buffers.Binary;

namespace CalibrationForge.Core;

public sealed class Unsigned16Table
{
    public Unsigned16Table(string name, int offset, int rows, int columns, int numerator, int denominator, string unit,
        TableAxis? rowAxis = null, TableAxis? columnAxis = null, string description = "", string valueName = "Cell value",
        string axisQualification = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (offset < 0 || rows <= 0 || columns <= 0 || numerator <= 0 || denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offsets, dimensions and scale must be valid.");
        }
        if (rowAxis is not null && rowAxis.Values.Count != rows) { throw new ArgumentException("Row axis count must match rows.", nameof(rowAxis)); }
        if (columnAxis is not null && columnAxis.Values.Count != columns) { throw new ArgumentException("Column axis count must match columns.", nameof(columnAxis)); }

        Name = name;
        Offset = offset;
        Rows = rows;
        Columns = columns;
        Numerator = numerator;
        Denominator = denominator;
        Unit = unit;
        RowAxis = rowAxis;
        ColumnAxis = columnAxis;
        Description = description;
        ValueName = valueName;
        AxisQualification = axisQualification;
        ByteLength = checked(rows * columns * sizeof(ushort));
        _ = checked(offset + ByteLength);
    }

    public string Name { get; }
    public int Offset { get; }
    public int Rows { get; }
    public int Columns { get; }
    public int Numerator { get; }
    public int Denominator { get; }
    public string Unit { get; }
    public TableAxis? RowAxis { get; }
    public TableAxis? ColumnAxis { get; }
    public string Description { get; }
    public string ValueName { get; }
    public string AxisQualification { get; }
    public int ByteLength { get; }
    public decimal Maximum => Decode(ushort.MaxValue);
    public decimal Decode(ushort raw) => (decimal)raw * Numerator / Denominator;

    // Quantize to the nearest raw integer; exact midpoints round away from zero.
    public ushort Encode(decimal value)
    {
        if (value < 0 || value > Maximum) { throw new ArgumentOutOfRangeException(nameof(value), $"Encoding bounds: 0–{Maximum} {Unit}; not safe tuning limits."); }
        return checked((ushort)decimal.Round(value * Denominator / Numerator, 0, MidpointRounding.AwayFromZero));
    }

    public int CellOffset(int row, int column)
    {
        if (row < 0 || row >= Rows) { throw new ArgumentOutOfRangeException(nameof(row)); }
        if (column < 0 || column >= Columns) { throw new ArgumentOutOfRangeException(nameof(column)); }
        return Offset + (row * Columns + column) * sizeof(ushort);
    }

    public ushort ReadRaw(ReadOnlySpan<byte> image, int row, int column)
    {
        ValidateImage(image.Length);
        return BinaryPrimitives.ReadUInt16BigEndian(image.Slice(CellOffset(row, column), sizeof(ushort)));
    }

    public decimal Read(ReadOnlySpan<byte> image, int row, int column) => Decode(ReadRaw(image, row, column));

    public void Write(Span<byte> image, int row, int column, decimal value)
    {
        ValidateImage(image.Length);
        var offset = CellOffset(row, column);
        var raw = Encode(value);
        BinaryPrimitives.WriteUInt16BigEndian(image.Slice(offset, sizeof(ushort)), raw);
    }

    private void ValidateImage(int length)
    {
        if (length < Offset + ByteLength) { throw new ArgumentException("Image is shorter than the complete table."); }
    }
}
