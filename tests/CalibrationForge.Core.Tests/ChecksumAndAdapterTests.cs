using System.Security.Cryptography;
using CalibrationForge.Core;

namespace CalibrationForge.Core.Tests;

public sealed class ChecksumAndAdapterTests
{
    private static readonly byte[] Reference = Convert.FromHexString("FFFA000100020003AABB");

    [Fact]
    public void AdditiveAnalysisDetectsModifiedCoveredBytesWithoutRepairingThem()
    {
        var candidate = Reference.ToArray();
        candidate[3] = 2;
        var before = candidate.ToArray();

        var result = Assert.Single(AdditiveChecksumAnalysis.Analyze(Reference, candidate, Profile()).Regions);

        Assert.Equal(0xFFFA, result.Stored);
        Assert.Equal(0xFFF9, result.Expected);
        Assert.Equal(1, result.Residual);
        Assert.Equal(1, result.ChangedBytes);
        Assert.False(result.MatchesObservedRule);
        Assert.Equal(before, candidate);
    }

    [Fact]
    public void ChangesOutsideCoverageAreNotClaimedAsValidated()
    {
        var candidate = Reference.ToArray();
        candidate[9] ^= 1;

        var report = AdditiveChecksumAnalysis.Analyze(Reference, candidate, Profile());

        Assert.True(report.Regions[0].MatchesObservedRule);
        Assert.Equal(1, report.ChangesOutsideCoverage);
    }

    [Fact]
    public void UnknownReferenceAndChangedDirectoryInvalidateCoverage()
    {
        var wrongReference = Reference.ToArray();
        wrongReference[0] ^= 1;
        Assert.Throws<FormatException>(() => AdditiveChecksumAnalysis.Analyze(wrongReference, Reference, Profile()));
        var candidate = Reference.ToArray();
        candidate[9] ^= 1;
        Assert.Throws<FormatException>(() => AdditiveChecksumAnalysis.Analyze(Reference, candidate, Profile(protectMetadata: true)));
        Assert.Throws<FormatException>(() => AdditiveChecksumAnalysis.Analyze(Reference, Reference));
    }

    [Fact]
    public void BigEndianWordSumWrapsAndRejectsPartialWords()
    {
        Assert.Equal(0, AdditiveChecksumAnalysis.SumBigEndianWords(Convert.FromHexString("FFFF0001")));
        Assert.Equal(0x1234, AdditiveChecksumAnalysis.SumBigEndianWords(Convert.FromHexString("1234")));
        Assert.Throws<ArgumentException>(() => AdditiveChecksumAnalysis.SumBigEndianWords(new byte[] { 1 }));
    }

    [Fact]
    public void BasicObdSupportIsNotRawKernelReadSupport()
    {
        var capabilities = new Dictionary<AdapterCapability, CapabilitySupport>
        {
            [AdapterCapability.PcConnectivity] = CapabilitySupport.Supported,
            [AdapterCapability.StandardObdMode09] = CapabilitySupport.Supported,
            [AdapterCapability.CancellableIo] = CapabilitySupport.Supported,
            [AdapterCapability.CalibrationForgeDriver] = CapabilitySupport.Supported
        };
        var adapter = new AdapterCapabilities("Synthetic basic OBD driver", capabilities);
        capabilities[AdapterCapability.RawClassicCan11Bit] = CapabilitySupport.Supported;

        Assert.True(AdapterRequirements.Assess(adapter, AdapterUse.ObdIdentification).RequirementsMet);
        var kernel = AdapterRequirements.Assess(adapter, AdapterUse.E92ReadKernelTransport);
        Assert.False(kernel.RequirementsMet);
        Assert.Contains(kernel.Results, item => item.Support == CapabilitySupport.Unknown);
        Assert.Equal(CapabilitySupport.Unknown, adapter.Support(AdapterCapability.RawClassicCan11Bit));
    }

    [Fact]
    public void UnknownOrUnimplementedDriverPreventsCapabilityApproval()
    {
        var features = Enum.GetValues<AdapterCapability>().ToDictionary(capability => capability, _ => CapabilitySupport.Supported);
        features.Remove(AdapterCapability.CalibrationForgeDriver);
        Assert.False(AdapterRequirements.Assess(new AdapterCapabilities("No driver", features), AdapterUse.E92ReadKernelTransport).RequirementsMet);
        features[AdapterCapability.CalibrationForgeDriver] = CapabilitySupport.NotSupported;
        Assert.False(AdapterRequirements.Assess(new AdapterCapabilities("Unavailable driver", features), AdapterUse.ObdIdentification).RequirementsMet);
        features[AdapterCapability.CalibrationForgeDriver] = CapabilitySupport.Supported;
        var result = AdapterRequirements.Assess(new AdapterCapabilities("Declared features only", features), AdapterUse.E92ReadKernelTransport);
        Assert.True(result.RequirementsMet);
        Assert.Contains("unvalidated", result.Conclusion);
    }

    private static AdditiveChecksumProfile Profile(bool protectMetadata = false) => new(
        "Synthetic checksum evidence", Reference.Length, Convert.ToHexString(SHA256.HashData(Reference)),
        new[] { new ChecksumRegion("Synthetic region", new MemoryRegion(0, 8)) },
        protectMetadata ? new[] { new MemoryRegion(8, 2) } : Array.Empty<MemoryRegion>(), "Synthetic arithmetic fixture; not E92 validation");
}
