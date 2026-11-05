using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CalibrationForge.Core;

public sealed record ChecksumRegion(string Name, MemoryRegion Region);

public sealed class AdditiveChecksumProfile
{
    public AdditiveChecksumProfile(string name, int imageBytes, string referenceSha256,
        IEnumerable<ChecksumRegion> regions, IEnumerable<MemoryRegion> protectedMetadata, string evidence)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(evidence) ||
            imageBytes is < 1 or > MemoryReadPlan.MaximumImageBytes || referenceSha256 is null ||
            referenceSha256.Length != 64 || referenceSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Checksum evidence needs a name, bounded size, exact reference SHA256, and attribution.");
        }
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(protectedMetadata);
        var spans = regions.ToArray();
        var metadata = protectedMetadata.ToArray();
        if (spans.Length == 0 || spans.Any(span => span is null || string.IsNullOrWhiteSpace(span.Name) ||
            span.Region is null || span.Region.Length < 2 || (span.Region.Length & 1) != 0 || span.Region.EndExclusive > (uint)imageBytes) ||
            metadata.Any(span => span is null || span.EndExclusive > (uint)imageBytes))
        {
            throw new ArgumentException("Checksum regions must fit the image and contain whole big-endian words.");
        }
        var ordered = spans.OrderBy(span => span.Region.Address).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].Region.Address < ordered[index - 1].Region.EndExclusive)
            {
                throw new ArgumentException("Checksum coverage regions cannot overlap.");
            }
        }
        Name = name;
        ImageBytes = imageBytes;
        ReferenceSha256 = referenceSha256;
        Regions = Array.AsReadOnly(spans);
        ProtectedMetadata = Array.AsReadOnly(metadata);
        Evidence = evidence;
    }

    public string Name { get; }
    public int ImageBytes { get; }
    public string ReferenceSha256 { get; }
    public IReadOnlyList<ChecksumRegion> Regions { get; }
    public IReadOnlyList<MemoryRegion> ProtectedMetadata { get; }
    public string Evidence { get; }

    public static AdditiveChecksumProfile E92Sample { get; } = new(
        "Observed additive checks in the exact 12691156 public sample",
        ExperimentalBinProfile.E92Sample.Size, ExperimentalBinProfile.E92Sample.Sha256,
        new[]
        {
            new ChecksumRegion("Calibration at 0x40000", new MemoryRegion(0x40000, 0x3000)),
            new ChecksumRegion("Calibration at 0x43000", new MemoryRegion(0x43000, 0x5000)),
            new ChecksumRegion("Calibration at 0x48000", new MemoryRegion(0x48000, 0x2000)),
            new ChecksumRegion("Calibration at 0x4A000", new MemoryRegion(0x4A000, 0x15F00)),
            new ChecksumRegion("Engine / dwell segment (sample)", new MemoryRegion(0x5FF00, 0x60100))
        }, new[]
        {
            new MemoryRegion(0xC0100, 0x100), new MemoryRegion(0x40005, 4),
            new MemoryRegion(0x43005, 4), new MemoryRegion(0x48005, 4),
            new MemoryRegion(0x4A005, 4), new MemoryRegion(0x5FF05, 4),
            new MemoryRegion(0xAC7EC, 4)
        },
        ExperimentalBinProfile.FixtureSource + " - all five regions have a zero BE16 word-sum residual; this does not establish the full checksum scheme.");
}

public sealed record AdditiveChecksumResult(string Name, uint Address, int Length, ushort Stored,
    ushort Expected, ushort Residual, int ChangedBytes)
{
    public bool MatchesObservedRule => Residual == 0;
}

public sealed record AdditiveChecksumReport(string Profile, string Evidence,
    IReadOnlyList<AdditiveChecksumResult> Regions, int ChangesOutsideCoverage);

public static class AdditiveChecksumAnalysis
{
    public static ushort SumBigEndianWords(ReadOnlySpan<byte> bytes)
    {
        if ((bytes.Length & 1) != 0) { throw new ArgumentException("Additive checksum input must contain complete 16-bit words."); }
        uint total = 0;
        for (var index = 0; index < bytes.Length; index += 2)
        {
            total = (total + BinaryPrimitives.ReadUInt16BigEndian(bytes[index..])) & 0xFFFF;
        }
        return (ushort)total;
    }

    public static AdditiveChecksumReport Analyze(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> candidate,
        AdditiveChecksumProfile? profile = null)
    {
        profile ??= AdditiveChecksumProfile.E92Sample;
        if (reference.Length != profile.ImageBytes || candidate.Length != profile.ImageBytes ||
            !Convert.ToHexString(SHA256.HashData(reference)).Equals(profile.ReferenceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Checksum analysis requires the exact hash-bound original reference and an equally sized candidate.");
        }
        foreach (var region in profile.ProtectedMetadata)
        {
            if (!reference.Slice((int)region.Address, region.Length).SequenceEqual(candidate.Slice((int)region.Address, region.Length)))
            {
                throw new FormatException("Protected segment metadata changed. Checksum coverage is no longer established.");
            }
        }
        var results = new List<AdditiveChecksumResult>();
        var coveredChanges = 0;
        foreach (var segment in profile.Regions)
        {
            var region = segment.Region;
            if (SumBigEndianWords(reference.Slice((int)region.Address, region.Length)) != 0)
            {
                throw new FormatException("Reference does not satisfy the profile's observed additive rule.");
            }
            var span = candidate.Slice((int)region.Address, region.Length);
            var changed = 0;
            for (var index = 0; index < span.Length; index++)
            {
                if (span[index] != reference[(int)region.Address + index]) { changed++; }
            }
            coveredChanges += changed;
            results.Add(new AdditiveChecksumResult(segment.Name, region.Address, region.Length,
                BinaryPrimitives.ReadUInt16BigEndian(span), unchecked((ushort)-SumBigEndianWords(span[2..])),
                SumBigEndianWords(span), changed));
        }
        var totalChanges = 0;
        for (var index = 0; index < reference.Length; index++)
        {
            if (reference[index] != candidate[index]) { totalChanges++; }
        }
        return new AdditiveChecksumReport(profile.Name, profile.Evidence, results.AsReadOnly(), totalChanges - coveredChanges);
    }
}
