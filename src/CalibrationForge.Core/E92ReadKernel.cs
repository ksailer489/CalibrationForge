using System.Buffers.Binary;

namespace CalibrationForge.Core;

public sealed record ProtocolEvidence(string Topic, string Finding, string Source);

public static class E92ReadResearch
{
    public const int FlashBytes = 4 * 1024 * 1024;
    public const string SourceCommit = "de16ff45e4c6bc905bce987f286637272f1aed94";
    private const string Root = "https://github.com/enslaved87/sCANtool-public/blob/" + SourceCommit + "/";
    public static IReadOnlyList<MemoryRegion> EccHoles { get; } = Array.AsReadOnly(new[]
    {
        new MemoryRegion(0x1FFF8, 8), new MemoryRegion(0x2FFF8, 8)
    });
    public static IReadOnlyList<ProtocolEvidence> Evidence { get; } = Array.AsReadOnly(new[]
    {
        new ProtocolEvidence("Transport", "Public reader uses 500 kbit/s classic CAN, request 7E0 / response 7E8.",
            Root + "scantool_public/vendor/e92/read_kernel/main.c"),
        new ProtocolEvidence("Bootstrap", "Public workflow unlocks the controller and uploads a RAM reader at 40001000. CalibrationForge does not implement security access, upload, or recovery.",
            Root + "scantool_public/vendor/e92/read_kernel/README.md"),
        new ProtocolEvidence("Memory-read contract", "Already-running SCPB-compatible kernel: 23 + BE32 address + BE16 length; response 63 + BE32 address + data; maximum 2048 bytes. This is not stock UDS ALFI framing.",
            Root + "scantool_public/vendor/e92/read_kernel/main.c"),
        new ProtocolEvidence("Coverage", "Two documented eight-byte ECC holes remain unknown. The SCPB alive signature does not authenticate the R2e kernel version.",
            Root + "scantool_public/vendor/e92/read_kernel/README.md"),
        new ProtocolEvidence("Validation", "Source-documented behavior and project bench claims only; no CalibrationForge hardware validation or licensed kernel distribution.",
            Root + "scantool_public/features/e92_read.py")
    });

    public static MemoryReadPlan FlashPlan(uint address = 0, int length = FlashBytes, int chunkSize = 2048)
    {
        var region = new MemoryRegion(address, length);
        if (region.EndExclusive > FlashBytes || chunkSize > E92ReadKernelCodec.MaximumBlockBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Experimental plan must stay within 4 MiB flash and use chunks of at most 2048 bytes.");
        }
        var exclusions = EccHoles.Where(hole => hole.Address < region.EndExclusive && hole.EndExclusive > address)
            .Select(hole => new MemoryRegion(Math.Max(address, hole.Address),
                (int)(Math.Min(region.EndExclusive, hole.EndExclusive) - Math.Max(address, hole.Address))));
        return new MemoryReadPlan(address, length, chunkSize, exclusions);
    }
}

public static class E92ReadKernelCodec
{
    public const int MaximumBlockBytes = 2048;

    public static byte[] EncodeRead(uint address, int length)
    {
        var region = new MemoryRegion(address, length);
        if (length > MaximumBlockBytes || region.EndExclusive > E92ReadResearch.FlashBytes ||
            E92ReadResearch.EccHoles.Any(hole => hole.Address < region.EndExclusive && hole.EndExclusive > address))
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Read must fit flash, stay within 2048 bytes, and avoid documented ECC holes.");
        }
        var request = new byte[7];
        request[0] = 0x23;
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(5), (ushort)length);
        return request;
    }

    public static MemoryBlock DecodeRead(ReadOnlySpan<byte> payload, uint address, int length)
    {
        _ = EncodeRead(address, length);
        if (payload.Length != 5 + length || payload[0] != 0x63 ||
            BinaryPrimitives.ReadUInt32BigEndian(payload[1..]) != address)
        {
            throw new MemoryReadIntegrityException($"Kernel reply for 0x{address:X8} has the wrong service, address, or byte count.");
        }
        return new MemoryBlock(address, payload[5..]);
    }

    public static void ValidateProbe(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is not (6 or 7) || !payload[..6].SequenceEqual(new byte[] { 0x5A, 0xBB, 0x53, 0x43, 0x50, 0x42 }) ||
            (payload.Length == 7 && payload[6] != 0))
        {
            throw new FormatException("SCPB-compatible RAM read kernel was not identified. No memory-read request is allowed.");
        }
    }
}

/// <summary>Uses an already-running SCPB-compatible RAM reader; performs no security access, upload, or reset.</summary>
public sealed class E92ReadKernelReader : IMemoryReader
{
    private readonly IDiagnosticChannel channel;

    private E92ReadKernelReader(IDiagnosticChannel channel, MemoryReadSource source)
    {
        this.channel = channel;
        Source = source;
    }

    public MemoryReadSource Source { get; }
    public int MaximumReadLength => E92ReadKernelCodec.MaximumBlockBytes;
    public bool RequiresReconnect => channel.RequiresReconnect;

    public static async Task<E92ReadKernelReader> ProbeAsync(IDiagnosticChannel channel, MemoryReadSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(source);
        if (channel.RequestId != 0x7E0 || channel.ResponseId != 0x7E8 ||
            string.IsNullOrWhiteSpace(source.Name) || source.SessionId == Guid.Empty || !Enum.IsDefined(source.Kind))
        {
            throw new ArgumentException("Research contract requires 7E0/7E8 addressing and explicit source/session provenance.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var completed = false;
        try
        {
            E92ReadKernelCodec.ValidateProbe(
                await channel.ExchangeAsync(new byte[] { 0x1A, 0xBB }, cancellationToken).ConfigureAwait(false));
            completed = true;
            return new E92ReadKernelReader(channel, source);
        }
        finally { if (!completed) { channel.Invalidate(); } }
    }

    public async Task<MemoryBlock> ReadAsync(uint address, int length, CancellationToken cancellationToken)
    {
        var request = E92ReadKernelCodec.EncodeRead(address, length);
        cancellationToken.ThrowIfCancellationRequested();
        var completed = false;
        try
        {
            var reply = await channel.ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
            var block = E92ReadKernelCodec.DecodeRead(reply, address, length);
            completed = true;
            return block;
        }
        finally { if (!completed) { channel.Invalidate(); } }
    }
}
