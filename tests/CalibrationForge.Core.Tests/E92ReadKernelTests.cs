using CalibrationForge.Core;

namespace CalibrationForge.Core.Tests;

public sealed class E92ReadKernelTests
{
    [Fact]
    public void ReadRequestUsesTheDocumentedKernelFormatNotStockUdsAlfi()
    {
        Assert.Equal(Convert.FromHexString("23000001000800"), E92ReadKernelCodec.EncodeRead(0x100, 2048));
        Assert.Equal(Convert.FromHexString("23000123450008"), E92ReadKernelCodec.EncodeRead(0x12345, 8));
    }

    [Fact]
    public void OrdinaryFlashTailsRemainReadableButEccHolesAreNotRequested()
    {
        Assert.Equal(7, E92ReadKernelCodec.EncodeRead(0x2EF800, 0x800).Length);
        Assert.Equal(7, E92ReadKernelCodec.EncodeRead(0x1F800, 0x7F8).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => E92ReadKernelCodec.EncodeRead(0x1F800, 0x800));
        Assert.Throws<ArgumentOutOfRangeException>(() => E92ReadKernelCodec.EncodeRead(0x2FFF8, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => E92ReadKernelCodec.EncodeRead(0x3FFFFF, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => E92ReadKernelCodec.EncodeRead(0x100, 2049));
    }

    [Fact]
    public void FullPlanAccountsForExactlySixteenUnknownBytes()
    {
        var plan = E92ReadResearch.FlashPlan();
        Assert.Equal(E92ReadResearch.FlashBytes - 16, plan.ReadableBytes);
        Assert.Equal(new uint[] { 0x1FFF8, 0x2FFF8 }, plan.Exclusions.Select(hole => hole.Address));
        Assert.Equal(plan.ReadableBytes, plan.Chunks().Sum(chunk => chunk.Length));
        Assert.DoesNotContain(plan.Chunks(), chunk => plan.Exclusions.Any(hole => hole.Address < chunk.EndExclusive && hole.EndExclusive > chunk.Address));
    }

    [Fact]
    public void RequestedRangesClipHolesInsteadOfDroppingTheirReadableTails()
    {
        var plan = E92ReadResearch.FlashPlan(0x1FFFC, 8, 4);
        var hole = Assert.Single(plan.Exclusions);
        Assert.Equal(0x1FFFCU, hole.Address);
        Assert.Equal(4, hole.Length);
        var chunk = Assert.Single(plan.Chunks());
        Assert.Equal(0x20000U, chunk.Address);
        Assert.Equal(4, chunk.Length);
    }

    [Theory]
    [InlineData("6300000100AABB")]
    [InlineData("6300000104AABBCCDD")]
    [InlineData("7F23110000AABBCCDD")]
    [InlineData("6300000100AABBCCDDEE")]
    public void WrongAddressesCountsOrServicesFailWithoutSubstitutingData(string hex)
    {
        Assert.Throws<MemoryReadIntegrityException>(() => E92ReadKernelCodec.DecodeRead(Convert.FromHexString(hex), 0x100, 4));
    }

    [Fact]
    public async Task SourceContractFixtureExercisesProbeFlowControlTwoPassesAndAddressEcho()
    {
        var trace = CanTraceReplay.Parse(E92ReadKernelExample.Json);
        var source = new MemoryReadSource(trace.Source, MemorySourceKind.Simulated, Guid.NewGuid());
        var reader = await E92ReadKernelReader.ProbeAsync(new IsoTpSession(trace), source);
        var dump = await new MemoryReadCoordinator(reader).ReadVerifiedAsync(
            E92ReadResearch.FlashPlan(E92ReadKernelExample.Address, E92ReadKernelExample.Length, E92ReadKernelExample.ChunkSize));

        Assert.Equal(Convert.FromHexString("AABBCCDD11223344"), dump.CopyImage());
        Assert.Equal(source, dump.Source);
        trace.AssertComplete();
    }

    [Fact]
    public async Task FailedProbeCannotCreateAReaderOrSendMemoryReads()
    {
        var trace = CanTraceReplay.Parse(E92ReadKernelExample.Json.Replace("07 5A BB 53 43 50 42 00", "07 5A BB 53 43 50 58 00"));
        var session = new IsoTpSession(trace);

        await Assert.ThrowsAsync<FormatException>(() => E92ReadKernelReader.ProbeAsync(session,
            new MemoryReadSource("Bad signature fixture", MemorySourceKind.Simulated, Guid.NewGuid())));

        Assert.True(session.RequiresReconnect);
        Assert.Equal(16, trace.RemainingFrames);
    }

    [Fact]
    public async Task CorruptReadAddressInvalidatesTheChannelAndPreventsRetry()
    {
        var trace = CanTraceReplay.Parse(E92ReadKernelExample.Json.Replace("10 09 63 00 00 01 00 AA", "10 09 63 00 00 01 01 AA"));
        var session = new IsoTpSession(trace);
        var reader = await E92ReadKernelReader.ProbeAsync(session,
            new MemoryReadSource("Corrupt address fixture", MemorySourceKind.Simulated, Guid.NewGuid()));
        await Assert.ThrowsAsync<MemoryReadIntegrityException>(() =>
            new MemoryReadCoordinator(reader, retries: 3).ReadVerifiedAsync(E92ReadResearch.FlashPlan(0x100, 8, 4)));
        Assert.True(reader.RequiresReconnect);
        Assert.Equal(12, trace.RemainingFrames);
    }
}
