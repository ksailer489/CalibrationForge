using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CalibrationForge.Core;

public sealed class ExperimentalBinProfile
{
    public const string Attribution = "Community-derived DwellMain*17- and KtFEQR_afr_StoichAirFuelRatio metadata: https://github.com/joukoy/UniversalPatcher/blob/de421df36e195c3afc45e589aea0210afdf6beff/XML/TableSeek-e92.xml";
    public const string FixtureSource = "https://github.com/PcmHammer/PcmHammer/blob/develop/Apps/Tests/TestData/E92_4096KiB_12691156.bin";
    public static ExperimentalBinProfile E92Sample { get; } = new(
        "E92 public sample / part 12691156 (experimental)", 4194304,
        "a9ccb739c643880eea2794ef5a579b70a7c292c62e10d4e140ef38d8963d893b",
        new Unsigned16Table("Base ignition dwell (coil charge time)", 0xAC7F0, 33, 17, 1, 128, "ms",
            new TableAxis("Ignition voltage", "V", Enumerable.Range(0, 33).Select(value => (decimal)value)),
            new TableAxis("Engine speed", "RPM", Enumerable.Range(0, 17).Select(value => (decimal)value * 500)),
            "Coil charge time before a spark; each cell is milliseconds at the indicated voltage and engine speed.",
            "Coil charge time", "Community-defined axes; not OEM/hardware validated"), true,
        [new Unsigned16Table("Stoichiometric air/fuel ratio", 0x842CC, 17, 1, 1, 2048, "AFR",
            new TableAxis("Fuel alcohol content", "%", Enumerable.Range(0, 17)
                .Select(index => index * 100m * 65535m / (16m * 65536m))),
            null,
            "Fuel-composition reference for stoichiometry, not a commanded power-enrichment target or a power adjustment. Editing requires independent fueling validation.",
            "Stoichiometric air/fuel ratio", "Community-derived fuel-alcohol axis; not OEM/hardware validated",
            TableHeaderKind.Count)]);

    internal ExperimentalBinProfile(string name, int size, string sha256, Unsigned16Table table, bool validateSegments = false,
        IEnumerable<Unsigned16Table>? additionalTables = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var tables = new[] { table }.Concat(additionalTables ?? []).ToArray();
        if (tables.Any(item => item is null)) { throw new ArgumentException("Definitions cannot be null.", nameof(additionalTables)); }
        var ordered = tables.OrderBy(item => item.Offset - item.HeaderByteLength).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].Offset - ordered[index].HeaderByteLength < ordered[index - 1].Offset + ordered[index - 1].ByteLength)
            {
                throw new ArgumentException("Table headers and data must not overlap.", nameof(additionalTables));
            }
        }
        Name = name;
        Size = size;
        Sha256 = sha256;
        Table = table;
        Tables = Array.AsReadOnly(tables);
        ValidateSegments = validateSegments;
    }

    public string Name { get; }
    public int Size { get; }
    public string Sha256 { get; }
    public Unsigned16Table Table { get; }
    public IReadOnlyList<Unsigned16Table> Tables { get; }
    private bool ValidateSegments { get; }

    internal bool Matches(ReadOnlySpan<byte> image)
    {
        if (image.Length != Size || !Convert.ToHexString(SHA256.HashData(image)).Equals(Sha256, StringComparison.OrdinalIgnoreCase)) { return false; }
        foreach (var table in Tables)
        {
            if (table.Offset < table.HeaderByteLength || table.Offset + table.ByteLength > image.Length) { return false; }
            if (table.HeaderKind == TableHeaderKind.Count)
            {
                if (BinaryPrimitives.ReadUInt16BigEndian(image.Slice(table.Offset - 2, 2)) != table.Rows * table.Columns) { return false; }
            }
            else if (BinaryPrimitives.ReadUInt16BigEndian(image.Slice(table.Offset - 4, 2)) != table.Columns ||
                BinaryPrimitives.ReadUInt16BigEndian(image.Slice(table.Offset - 2, 2)) != table.Rows) { return false; }
        }
        if (!ValidateSegments) { return true; }
        return Pair(image, 0xC0126, 0xC0000, 0xFFFFF) &&
            Pair(image, 0xC0162, 0x40000, 0x42FFF) &&
            Pair(image, 0xC0186, 0x43000, 0x47FFF) &&
            Pair(image, 0xC01AA, 0x48000, 0x49FFF) &&
            Pair(image, 0xC01CE, 0x4A000, 0x5FEFF) &&
            Pair(image, 0xC01F2, 0x5FF00, 0xBFFFF) &&
            U32(image, 0xC0105) == 12691156 &&
            U32(image, 0x40005) == 12675364 && U32(image, 0x43005) == 12675313 &&
            U32(image, 0x48005) == 12675457 && U32(image, 0x4A005) == 12686582 &&
            U32(image, 0x5FF05) == 12686263;
    }

    private static uint U32(ReadOnlySpan<byte> image, int offset) => BinaryPrimitives.ReadUInt32BigEndian(image.Slice(offset, 4));
    private static bool Pair(ReadOnlySpan<byte> image, int offset, uint start, uint end) => U32(image, offset) == start && U32(image, offset + 4) == end;
}
