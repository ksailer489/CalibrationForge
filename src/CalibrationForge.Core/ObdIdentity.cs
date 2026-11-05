using System.Buffers.Binary;
using System.Text;

namespace CalibrationForge.Core;

public sealed record ObdIdentity(uint RequestId, uint ResponseId, string? Vin,
    IReadOnlyList<string>? CalibrationIds, IReadOnlyList<uint>? CalibrationVerificationNumbers);

public static class ObdIdentityDecoder
{
    public static uint DecodeSupportedPids(ReadOnlySpan<byte> payload)
    {
        Validate(payload, 0x00);
        if (payload.Length != 6) { throw new FormatException("Mode 09 PID 00 requires a four-byte support bitmap."); }
        return BinaryPrimitives.ReadUInt32BigEndian(payload[2..]);
    }

    public static bool IsSupported(uint bitmap, byte pid)
    {
        if (pid is < 1 or > 32) { throw new ArgumentOutOfRangeException(nameof(pid)); }
        return (bitmap & (1u << (32 - pid))) != 0;
    }

    public static string DecodeVin(ReadOnlySpan<byte> payload)
    {
        Validate(payload, 0x02);
        if (payload.Length != 20 || payload[2] != 1) { throw new FormatException("CAN VIN response requires one 17-character VIN."); }
        foreach (var character in payload[3..])
        {
            if (!(character is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z') ||
                character is (byte)'I' or (byte)'O' or (byte)'Q')
            {
                throw new FormatException("VIN contains an invalid character.");
            }
        }
        return Encoding.ASCII.GetString(payload[3..]);
    }

    public static IReadOnlyList<string> DecodeCalibrationIds(ReadOnlySpan<byte> payload)
    {
        ValidateItems(payload, 0x04, 16);
        var ids = new List<string>();
        for (var index = 3; index < payload.Length; index += 16)
        {
            var bytes = payload.Slice(index, 16);
            var padding = false;
            foreach (var character in bytes)
            {
                if (character == 0) { padding = true; }
                else if (padding || character is < 0x20 or > 0x7E)
                {
                    throw new FormatException("Calibration ID contains invalid ASCII or non-trailing NUL padding.");
                }
            }
            var id = Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
            if (id.Length == 0) { throw new FormatException("Calibration ID cannot be empty."); }
            ids.Add(id);
        }
        return ids.AsReadOnly();
    }

    public static IReadOnlyList<uint> DecodeCvns(ReadOnlySpan<byte> payload)
    {
        ValidateItems(payload, 0x06, 4);
        var values = new List<uint>();
        for (var index = 3; index < payload.Length; index += 4)
        {
            values.Add(BinaryPrimitives.ReadUInt32BigEndian(payload[index..]));
        }
        return values.AsReadOnly();
    }

    private static void Validate(ReadOnlySpan<byte> payload, byte pid)
    {
        if (payload.Length >= 3 && payload[0] == 0x7F && payload[1] == 0x09)
        {
            throw new FormatException($"Controller rejected Mode 09 with negative response 0x{payload[2]:X2}.");
        }
        if (payload.Length < 2 || payload[0] != 0x49 || payload[1] != pid)
        {
            throw new FormatException($"Expected Mode 09 PID {pid:X2} response; controller identity is not confirmed.");
        }
    }

    private static void ValidateItems(ReadOnlySpan<byte> payload, byte pid, int itemSize)
    {
        Validate(payload, pid);
        if (payload.Length < 3 || payload[2] == 0 || payload.Length != 3 + payload[2] * itemSize)
        {
            throw new FormatException($"Mode 09 PID {pid:X2} count does not match its {itemSize}-byte entries.");
        }
    }
}

public sealed class ObdIdentityClient(IDiagnosticChannel session)
{
    private readonly IDiagnosticChannel session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<ObdIdentity> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.RequiresReconnect) { throw new InvalidOperationException("Reconnect before reading controller identity."); }
            var completed = false;
            try
            {
                var support = ObdIdentityDecoder.DecodeSupportedPids(
                    await session.ExchangeAsync(new byte[] { 0x09, 0x00 }, cancellationToken).ConfigureAwait(false));
                string? vin = null;
                IReadOnlyList<string>? ids = null;
                IReadOnlyList<uint>? cvns = null;
                if (ObdIdentityDecoder.IsSupported(support, 0x02))
                {
                    vin = ObdIdentityDecoder.DecodeVin(
                        await session.ExchangeAsync(new byte[] { 0x09, 0x02 }, cancellationToken).ConfigureAwait(false));
                }
                if (ObdIdentityDecoder.IsSupported(support, 0x04))
                {
                    ids = ObdIdentityDecoder.DecodeCalibrationIds(
                        await session.ExchangeAsync(new byte[] { 0x09, 0x04 }, cancellationToken).ConfigureAwait(false));
                }
                if (ObdIdentityDecoder.IsSupported(support, 0x06))
                {
                    cvns = ObdIdentityDecoder.DecodeCvns(
                        await session.ExchangeAsync(new byte[] { 0x09, 0x06 }, cancellationToken).ConfigureAwait(false));
                }
                completed = true;
                return new ObdIdentity(session.RequestId, session.ResponseId, vin, ids, cvns);
            }
            finally
            {
                if (!completed) { session.Invalidate(); }
            }
        }
        finally { gate.Release(); }
    }
}
