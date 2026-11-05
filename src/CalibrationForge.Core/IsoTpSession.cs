namespace CalibrationForge.Core;

public sealed class IsoTpSession : IDiagnosticChannel
{
    private const int ReceiveBlockSize = 8;
    private readonly ICanTransport transport;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan timeout;
    private volatile bool requiresReconnect;

    public IsoTpSession(ICanTransport transport, uint requestId = 0x7E0, uint responseId = 0x7E8,
        TimeSpan? timeout = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (requestId > 0x7FF || responseId > 0x7FF || requestId == responseId)
        {
            throw new ArgumentException("Select distinct standard 11-bit request and response IDs.");
        }
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Transaction timeout must be positive and at most two minutes.");
        }
        RequestId = requestId;
        ResponseId = responseId;
    }

    public uint RequestId { get; }
    public uint ResponseId { get; }
    public bool RequiresReconnect => requiresReconnect;

    public async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken = default)
    {
        var outgoing = IsoTpCodec.Encode(request);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (requiresReconnect)
            {
                throw new InvalidOperationException("This session failed. Reconnect with a fresh transport before another transaction.");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var completed = false;
            try
            {
                await SendRequestAsync(outgoing, deadline.Token).ConfigureAwait(false);
                var payload = await ReceivePayloadAsync(deadline.Token).ConfigureAwait(false);
                completed = true;
                return payload;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException($"ISO-TP transaction with 0x{ResponseId:X3} exceeded {timeout.TotalSeconds:g} seconds.");
            }
            finally
            {
                if (!completed) { requiresReconnect = true; }
            }
        }
        finally { gate.Release(); }
    }

    public void Invalidate() => requiresReconnect = true;

    private async Task SendRequestAsync(IReadOnlyList<byte[]> frames, CancellationToken cancellationToken)
    {
        await SendFrameAsync(frames[0], cancellationToken).ConfigureAwait(false);
        var index = 1;
        while (index < frames.Count)
        {
            var control = await ReceiveFlowControlAsync(cancellationToken).ConfigureAwait(false);
            var end = control.BlockSize == 0 ? frames.Count : Math.Min(frames.Count, index + control.BlockSize);
            while (index < end)
            {
                if (control.DelayMilliseconds > 0)
                {
                    await Task.Delay(control.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                await SendFrameAsync(frames[index++], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<(int BlockSize, int DelayMilliseconds)> ReceiveFlowControlAsync(CancellationToken cancellationToken)
    {
        var waits = 0;
        while (true)
        {
            var frame = await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Length < 3 || (frame[0] >> 4) != 3)
            {
                throw new IsoTpFrameException("Expected ISO-TP flow control before transmitting consecutive frames.");
            }
            var status = frame[0] & 0x0F;
            if (status == 1)
            {
                if (++waits > 3) { throw new IsoTpFrameException("Receiver exceeded the three-frame flow-control WAIT limit."); }
                continue;
            }
            if (status == 2) { throw new IsoTpFrameException("Receiver reported ISO-TP buffer overflow."); }
            if (status != 0) { throw new IsoTpFrameException("Receiver sent a reserved flow-control status."); }
            var separation = frame[2];
            if (separation <= 0x7F) { return (frame[1], separation); }
            // Round 100-900 microsecond STmin up to 1 ms rather than transmitting too early.
            if (separation is >= 0xF1 and <= 0xF9) { return (frame[1], 1); }
            throw new IsoTpFrameException("Receiver sent a reserved ISO-TP separation time.");
        }
    }

    private async Task<byte[]> ReceivePayloadAsync(CancellationToken cancellationToken)
    {
        var first = await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
        if (first.Length < 2) { throw new IsoTpFrameException("Response frame is too short."); }
        var frames = new List<byte[]> { first };
        if ((first[0] >> 4) == 0) { return IsoTpCodec.Decode(frames); }
        if (first.Length != 8 || (first[0] >> 4) != 1)
        {
            throw new IsoTpFrameException("Expected a classic CAN ISO-TP single or first frame.");
        }
        var length = ((first[0] & 0x0F) << 8) | first[1];
        if (length <= 7) { throw new IsoTpFrameException("Invalid or unsupported first-frame payload length."); }
        var remaining = length - 6;
        var sequence = 1;
        var receivedInBlock = 0;
        await SendFrameAsync(new byte[] { 0x30, ReceiveBlockSize, 0 }, cancellationToken).ConfigureAwait(false);
        while (remaining > 0)
        {
            var frame = await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            var count = Math.Min(remaining, 7);
            if (frame.Length < count + 1 || (frame[0] >> 4) != 2 || (frame[0] & 0x0F) != sequence)
            {
                throw new IsoTpFrameException($"Expected a complete consecutive frame with sequence {sequence}.");
            }
            frames.Add(frame);
            remaining -= count;
            sequence = (sequence + 1) & 0x0F;
            if (++receivedInBlock == ReceiveBlockSize && remaining > 0)
            {
                await SendFrameAsync(new byte[] { 0x30, ReceiveBlockSize, 0 }, cancellationToken).ConfigureAwait(false);
                receivedInBlock = 0;
            }
        }
        return IsoTpCodec.Decode(frames);
    }

    private ValueTask SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        var padded = new byte[8];
        frame.CopyTo(padded, 0);
        cancellationToken.ThrowIfCancellationRequested();
        return transport.SendAsync(new CanFrame(RequestId, padded), cancellationToken);
    }

    private async Task<byte[]> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Id == ResponseId) { return frame.Data.ToArray(); }
        }
    }
}
