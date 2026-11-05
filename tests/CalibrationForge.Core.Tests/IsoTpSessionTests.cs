using System.Text.Json;
using System.Diagnostics;
using CalibrationForge.Core;

namespace CalibrationForge.Core.Tests;

public sealed class IsoTpSessionTests
{
    [Fact]
    public async Task SingleRequestIsPaddedAndOnlyItsControllerResponseIsUsed()
    {
        var trace = Trace(
            Tx("02 09 00 00 00 00 00 00"),
            Rx("", "7E9"),
            Rx("06 49 00 54 00 00 00 AA"));
        var session = new IsoTpSession(trace);

        Assert.Equal(Convert.FromHexString("490054000000"), await session.ExchangeAsync(new byte[] { 0x09, 0x00 }));
        trace.AssertComplete();
        Assert.False(session.RequiresReconnect);
    }

    [Fact]
    public async Task MultiFrameResponseRequiresTesterFlowControlAndStripsPadding()
    {
        var trace = Trace(
            Tx("02 09 02 00 00 00 00 00"),
            Rx("10 08 49 02 01 54 45 53"),
            Tx("30 08 00 00 00 00 00 00"),
            Rx("21 54 30 AA AA AA AA AA"));

        Assert.Equal(Convert.FromHexString("4902015445535430"),
            await new IsoTpSession(trace).ExchangeAsync(new byte[] { 0x09, 0x02 }));
        trace.AssertComplete();
    }

    [Fact]
    public async Task ReceiverRenewsFlowControlAtBlockBoundariesAndAcceptsSequenceWrap()
    {
        var events = new List<object> { Tx("01 09 00 00 00 00 00 00"), Rx("10 7D 00 00 00 00 00 00") };
        for (var index = 1; index <= 17; index++)
        {
            if (index is 1 or 9 or 17) { events.Add(Tx("30 08 00 00 00 00 00 00")); }
            events.Add(Rx($"{0x20 | (index & 15):X2} 00 00 00 00 00 00 00"));
        }
        var trace = Trace(events.ToArray());

        Assert.Equal(new byte[125], await new IsoTpSession(trace).ExchangeAsync(new byte[] { 0x09 }));
        trace.AssertComplete();
    }

    [Fact]
    public async Task SenderWaitsForEachReceiverRequestedBlock()
    {
        var trace = Trace(
            Tx("10 14 00 01 02 03 04 05"),
            Rx("31 00 00"),
            Rx("30 01 00"),
            Tx("21 06 07 08 09 0A 0B 0C"),
            Rx("30 00 F1"),
            Tx("22 0D 0E 0F 10 11 12 13"),
            Rx("01 50"));

        Assert.Equal(new byte[] { 0x50 },
            await new IsoTpSession(trace).ExchangeAsync(Enumerable.Range(0, 20).Select(value => (byte)value).ToArray()));
        trace.AssertComplete();
    }

    [Fact]
    public async Task UnlimitedFlowControlDoesNotRequireAnExtraAcknowledgment()
    {
        var trace = Trace(
            Tx("10 14 00 01 02 03 04 05"), Rx("30 00 00"),
            Tx("21 06 07 08 09 0A 0B 0C"), Tx("22 0D 0E 0F 10 11 12 13"), Rx("01 50"));

        await new IsoTpSession(trace).ExchangeAsync(Enumerable.Range(0, 20).Select(value => (byte)value).ToArray());
        trace.AssertComplete();
    }

    [Fact]
    public async Task SenderPacesConsecutiveFramesUsingReceiverSeparationTime()
    {
        var transport = new TimedTransport();

        await new IsoTpSession(transport).ExchangeAsync(Enumerable.Range(0, 20).Select(value => (byte)value).ToArray());

        Assert.Equal(3, transport.TransmitTimes.Count);
        Assert.True(Stopwatch.GetElapsedTime(transport.TransmitTimes[0], transport.TransmitTimes[1]).TotalMilliseconds >= 19);
        Assert.True(Stopwatch.GetElapsedTime(transport.TransmitTimes[1], transport.TransmitTimes[2]).TotalMilliseconds >= 19);
    }

    [Theory]
    [InlineData("32 00 00")]
    [InlineData("33 00 00")]
    [InlineData("30 00 80")]
    [InlineData("30 00 F0")]
    [InlineData("30 00 FA")]
    [InlineData("30 00")]
    [InlineData("01 50")]
    public async Task InvalidFlowControlFailsBeforeAnyConsecutiveTransmit(string control)
    {
        var trace = Trace(Tx("10 08 00 01 02 03 04 05"), Rx(control));
        var session = new IsoTpSession(trace);

        await Assert.ThrowsAsync<IsoTpFrameException>(() => session.ExchangeAsync(Enumerable.Range(0, 8).Select(value => (byte)value).ToArray()));

        Assert.True(session.RequiresReconnect);
        trace.AssertComplete();
    }

    [Fact]
    public async Task ExcessiveFlowControlWaitsFailRatherThanHanging()
    {
        var trace = Trace(Tx("10 08 00 01 02 03 04 05"),
            Rx("31 00 00"), Rx("31 00 00"), Rx("31 00 00"), Rx("31 00 00"));

        await Assert.ThrowsAsync<IsoTpFrameException>(() =>
            new IsoTpSession(trace).ExchangeAsync(Enumerable.Range(0, 8).Select(value => (byte)value).ToArray()));
        trace.AssertComplete();
    }

    [Theory]
    [InlineData("22 00 00")]
    [InlineData("21 00")]
    [InlineData("30 00 00")]
    public async Task BadConsecutiveResponseFailsImmediately(string frame)
    {
        var trace = Trace(Tx("01 09 00 00 00 00 00 00"), Rx("10 08 00 00 00 00 00 00"),
            Tx("30 08 00 00 00 00 00 00"), Rx(frame));
        var session = new IsoTpSession(trace);

        await Assert.ThrowsAsync<IsoTpFrameException>(() => session.ExchangeAsync(new byte[] { 0x09 }));
        Assert.True(session.RequiresReconnect);
    }

    [Fact]
    public async Task MissingReplyAndRetryCannotReuseAPotentiallyStaleSession()
    {
        var trace = Trace(Tx("01 09 00 00 00 00 00 00"));
        var session = new IsoTpSession(trace);

        await Assert.ThrowsAsync<EndOfStreamException>(() => session.ExchangeAsync(new byte[] { 0x09 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExchangeAsync(new byte[] { 0x09 }));
        Assert.True(session.RequiresReconnect);
    }

    [Fact]
    public async Task TimeoutIsDistinctFromCallerCancellation()
    {
        var transport = new BlockingTransport();
        var session = new IsoTpSession(transport, timeout: TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAsync<TimeoutException>(() => session.ExchangeAsync(new byte[] { 0x09 }));

        Assert.Equal(1, transport.SendCount);
        Assert.True(session.RequiresReconnect);
    }

    [Fact]
    public async Task CancelingAQueuedRequestDoesNotInterruptTheActiveTransaction()
    {
        var transport = new BlockingTransport();
        var session = new IsoTpSession(transport);
        using var activeCancellation = new CancellationTokenSource();
        var active = session.ExchangeAsync(new byte[] { 0x09 }, activeCancellation.Token);
        await transport.ReceiveStarted.Task;
        using var queuedCancellation = new CancellationTokenSource();
        queuedCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ExchangeAsync(new byte[] { 0x09 }, queuedCancellation.Token));

        Assert.False(session.RequiresReconnect);
        Assert.Equal(1, transport.SendCount);
        activeCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.True(session.RequiresReconnect);
    }

    [Fact]
    public async Task ConcurrentTransactionsDoNotInterleaveOnTheSameTransport()
    {
        var transport = new CoordinatedTransport();
        var session = new IsoTpSession(transport);
        var first = session.ExchangeAsync(new byte[] { 0x09 });
        await transport.ReceiveStarted.Task;
        var second = session.ExchangeAsync(new byte[] { 0x01 });

        Assert.Equal(1, transport.SendCount);
        transport.FirstResponse.SetResult(new CanFrame(0x7E8, new byte[] { 0x01, 0x49 }));
        Assert.Equal(new byte[] { 0x49 }, await first);
        Assert.Equal(new byte[] { 0x41 }, await second);
        Assert.Equal(2, transport.SendCount);
    }

    [Fact]
    public async Task InvalidPayloadDoesNotConsumeOrInvalidateTheTransport()
    {
        var trace = Trace(Tx("01 09 00 00 00 00 00 00"), Rx("01 49"));
        var session = new IsoTpSession(trace);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ExchangeAsync(Array.Empty<byte>()));
        Assert.False(session.RequiresReconnect);
        Assert.Equal(new byte[] { 0x49 }, await session.ExchangeAsync(new byte[] { 0x09 }));
        trace.AssertComplete();
    }

    [Fact]
    public async Task TraceRejectsWrongRequestAndUnexpectedTransmitOrder()
    {
        var trace = Trace(Tx("02 09 02 00 00 00 00 00"));
        await Assert.ThrowsAsync<FormatException>(() => new IsoTpSession(trace).ExchangeAsync(new byte[] { 0x09, 0x04 }));
        Assert.Equal(1, trace.RemainingFrames);
        Assert.Throws<FormatException>(() => trace.AssertComplete());
        await Assert.ThrowsAsync<FormatException>(async () => await trace.ReceiveAsync(default));
    }

    [Fact]
    public void TraceRejectsExtendedAddressingUnknownFieldsAndMissingProvenance()
    {
        Assert.Throws<FormatException>(() => Trace(Rx("01 49", "18DAF110")));
        Assert.Throws<JsonException>(() => CanTraceReplay.Parse(ObdIdentityExample.Json.Replace("\"isSimulation\": true,", "")));
        Assert.Throws<JsonException>(() => CanTraceReplay.Parse(ObdIdentityExample.Json.Replace("\"frames\":", "\"extra\": 1, \"frames\":")));
    }

    private static CanTraceReplay Trace(params object[] frames) => CanTraceReplay.Parse(JsonSerializer.Serialize(new
    {
        format = "CalibrationForge.CanTrace.v1",
        source = "Synthetic protocol test",
        isSimulation = true,
        frames
    }));

    private static object Tx(string data) => new { direction = "tx", id = "7E0", data };
    private static object Rx(string data, string id = "7E8") => new { direction = "rx", id, data };

    private sealed class BlockingTransport : ICanTransport
    {
        public TaskCompletionSource ReceiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CanFrame> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount { get; private set; }

        public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<CanFrame> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReceiveStarted.TrySetResult();
            return new ValueTask<CanFrame>(response.Task.WaitAsync(cancellationToken));
        }
    }

    private sealed class CoordinatedTransport : ICanTransport
    {
        public TaskCompletionSource ReceiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CanFrame> FirstResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount { get; private set; }

        public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<CanFrame> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReceiveStarted.TrySetResult();
            return SendCount == 1 ? new ValueTask<CanFrame>(FirstResponse.Task.WaitAsync(cancellationToken))
                : ValueTask.FromResult(new CanFrame(0x7E8, new byte[] { 0x01, 0x41 }));
        }
    }

    private sealed class TimedTransport : ICanTransport
    {
        private int received;
        public List<long> TransmitTimes { get; } = new();

        public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransmitTimes.Add(Stopwatch.GetTimestamp());
            return ValueTask.CompletedTask;
        }

        public ValueTask<CanFrame> ReceiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CanFrame(0x7E8,
                received++ == 0 ? new byte[] { 0x30, 0, 20 } : new byte[] { 1, 0x50 }));
        }
    }
}
