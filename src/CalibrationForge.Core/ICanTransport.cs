namespace CalibrationForge.Core;

public sealed class CanFrame
{
    private readonly byte[] data;

    public CanFrame(uint id, ReadOnlySpan<byte> data)
    {
        if (id > 0x7FF) { throw new ArgumentOutOfRangeException(nameof(id), "Only standard 11-bit CAN IDs are supported."); }
        if (data.Length > 8) { throw new ArgumentOutOfRangeException(nameof(data), "Only classic CAN data frames are supported."); }
        Id = id;
        this.data = data.ToArray();
    }

    public uint Id { get; }
    public ReadOnlyMemory<byte> Data => data;
}

/// <summary>Provide a fresh, exclusively owned receive stream, ordered sends, and cancellable I/O.</summary>
public interface ICanTransport
{
    ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken);
    ValueTask<CanFrame> ReceiveAsync(CancellationToken cancellationToken);
}
