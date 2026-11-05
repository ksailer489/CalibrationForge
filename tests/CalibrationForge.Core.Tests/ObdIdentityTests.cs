using System.Text;
using CalibrationForge.Core;

namespace CalibrationForge.Core.Tests;

public sealed class ObdIdentityTests
{
    [Fact]
    public async Task IdentityRunsThroughTheCompleteCanTransactionPath()
    {
        var trace = CanTraceReplay.Parse(ObdIdentityExample.Json);
        var identity = await new ObdIdentityClient(new IsoTpSession(trace)).ReadAsync();

        Assert.Equal("TEST0000000000001", identity.Vin);
        Assert.Equal("EXAMPLE-CAL", Assert.Single(identity.CalibrationIds!));
        Assert.Equal(0x12345678u, Assert.Single(identity.CalibrationVerificationNumbers!));
        trace.AssertComplete();
    }

    [Fact]
    public async Task UnsupportedPidsAreNotQueriedOrPresentedAsMissingHealthyValues()
    {
        var channel = new ScriptedChannel(new byte[] { 0x49, 0, 0, 0, 0, 0 });

        var identity = await new ObdIdentityClient(channel).ReadAsync();

        Assert.Null(identity.Vin);
        Assert.Null(identity.CalibrationIds);
        Assert.Null(identity.CalibrationVerificationNumbers);
        Assert.Equal(new byte[] { 0x09, 0x00 }, Assert.Single(channel.Requests));
        Assert.False(channel.RequiresReconnect);
    }

    [Fact]
    public async Task HigherLevelClientCanBeTestedWithoutCanFramesOrHardware()
    {
        var channel = new ScriptedChannel(
            new byte[] { 0x49, 0, 0x40, 0, 0, 0 },
            new byte[] { 0x49, 2, 1 }.Concat(Encoding.ASCII.GetBytes("TEST0000000000001")).ToArray());

        var identity = await new ObdIdentityClient(channel).ReadAsync();

        Assert.Equal("TEST0000000000001", identity.Vin);
        Assert.Equal(new byte[] { 0x09, 0x02 }, channel.Requests[1]);
        Assert.Equal(2, channel.Requests.Count);
    }

    [Fact]
    public async Task WrongPidInvalidatesChannelAndDoesNotReturnPartialIdentity()
    {
        var channel = new ScriptedChannel(
            new byte[] { 0x49, 0, 0x40, 0, 0, 0 },
            new byte[] { 0x49, 4, 1, 0 });

        await Assert.ThrowsAsync<FormatException>(() => new ObdIdentityClient(channel).ReadAsync());

        Assert.True(channel.RequiresReconnect);
    }

    [Fact]
    public async Task CancelingAQueuedIdentityReadDoesNotInvalidateTheActiveRead()
    {
        var channel = new BlockingChannel();
        var client = new ObdIdentityClient(channel);
        var active = client.ReadAsync();
        await channel.RequestStarted.Task;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadAsync(cancellation.Token));

        Assert.False(channel.RequiresReconnect);
        channel.Reply.SetResult(new byte[] { 0x49, 0, 0, 0, 0, 0 });
        Assert.Null((await active).Vin);
        Assert.False(channel.RequiresReconnect);
    }

    [Fact]
    public void VinRejectsIllegalCharactersInsteadOfReplacingThem()
    {
        var payload = new byte[] { 0x49, 2, 1 }.Concat(Encoding.ASCII.GetBytes("TEST0000000000001")).ToArray();
        payload[3] = (byte)'I';
        Assert.Throws<FormatException>(() => ObdIdentityDecoder.DecodeVin(payload));
        payload[3] = 0xFF;
        Assert.Throws<FormatException>(() => ObdIdentityDecoder.DecodeVin(payload));
        payload[3] = (byte)'T';
        payload[2] = 0;
        Assert.Throws<FormatException>(() => ObdIdentityDecoder.DecodeVin(payload));
    }

    [Fact]
    public void MultipleCalibrationIdsRetainTheirOrderAndTrimOnlyTrailingPadding()
    {
        var payload = new byte[] { 0x49, 4, 2 }
            .Concat(Encoding.ASCII.GetBytes("ENGINE-CAL".PadRight(16, '\0')))
            .Concat(Encoding.ASCII.GetBytes("TRANS-CAL".PadRight(16, ' '))).ToArray();

        Assert.Equal(new[] { "ENGINE-CAL", "TRANS-CAL" }, ObdIdentityDecoder.DecodeCalibrationIds(payload));
        payload[5] = 0;
        Assert.Throws<FormatException>(() => ObdIdentityDecoder.DecodeCalibrationIds(payload));
    }

    [Fact]
    public void CvnsAreUnsignedBigEndianReportedValues()
    {
        var cvns = ObdIdentityDecoder.DecodeCvns(Convert.FromHexString("490602FEDCBA9800000000"));
        Assert.Equal(new uint[] { 0xFEDCBA98, 0 }, cvns);
    }

    [Theory]
    [InlineData("490400")]
    [InlineData("49040141")]
    [InlineData("490600")]
    [InlineData("49060212345678")]
    [InlineData("4906011234567800")]
    public void InvalidCountsAndLengthsFailExplicitly(string hex)
    {
        var payload = Convert.FromHexString(hex);
        if (payload[1] == 4) { Assert.Throws<FormatException>(() => ObdIdentityDecoder.DecodeCalibrationIds(payload)); }
        else { Assert.Throws<FormatException>(() => ObdIdentityDecoder.DecodeCvns(payload)); }
    }

    [Fact]
    public void NegativeAndWrongServiceResponsesNeverBecomeIdentityValues()
    {
        Assert.Throws<FormatException>(() => ObdIdentityDecoder.DecodeSupportedPids(Convert.FromHexString("7F0911")));
        Assert.Throws<FormatException>(() => ObdIdentityDecoder.DecodeSupportedPids(Convert.FromHexString("410054000000")));
        Assert.Throws<FormatException>(() => ObdIdentityDecoder.DecodeSupportedPids(Convert.FromHexString("4900540000")));
    }

    private sealed class ScriptedChannel(params byte[][] replies) : IDiagnosticChannel
    {
        private readonly Queue<byte[]> replies = new(replies);
        public uint RequestId => 0x7E0;
        public uint ResponseId => 0x7E8;
        public bool RequiresReconnect { get; private set; }
        public List<byte[]> Requests { get; } = new();
        public void Invalidate() => RequiresReconnect = true;

        public Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RequiresReconnect) { throw new InvalidOperationException("Channel is invalid."); }
            Requests.Add(request.ToArray());
            return Task.FromResult(replies.Dequeue());
        }
    }

    private sealed class BlockingChannel : IDiagnosticChannel
    {
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<byte[]> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint RequestId => 0x7E0;
        public uint ResponseId => 0x7E8;
        public bool RequiresReconnect { get; private set; }
        public void Invalidate() => RequiresReconnect = true;

        public Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken = default)
        {
            RequestStarted.TrySetResult();
            return Reply.Task.WaitAsync(cancellationToken);
        }
    }
}
