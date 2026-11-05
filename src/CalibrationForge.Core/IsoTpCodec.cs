namespace CalibrationForge.Core;

/// <summary>Encodes and reassembles classic CAN ISO-TP data frames, without transport or flow-control timing.</summary>
public static class IsoTpCodec
{
    public const int MaximumFrameLength = 8;
    public const int MaximumPayloadLength = 4095;

    public static IReadOnlyList<byte[]> Encode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length is < 1 or > MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload length must be 1 through 4095 bytes.");
        }

        var frames = new List<byte[]>();
        if (payload.Length <= 7)
        {
            var frame = new byte[payload.Length + 1];
            frame[0] = (byte)payload.Length;
            payload.CopyTo(frame, 1);
            frames.Add(frame);
            return frames;
        }

        var firstFrame = new byte[MaximumFrameLength];
        firstFrame[0] = (byte)(0x10 | (payload.Length >> 8));
        firstFrame[1] = (byte)(payload.Length & 0xFF);
        payload.AsSpan(0, 6).CopyTo(firstFrame.AsSpan(2));
        frames.Add(firstFrame);

        var sequence = 1;
        for (var offset = 6; offset < payload.Length; offset += 7)
        {
            var count = Math.Min(7, payload.Length - offset);
            var frame = new byte[count + 1];
            frame[0] = (byte)(0x20 | sequence);
            payload.AsSpan(offset, count).CopyTo(frame.AsSpan(1));
            frames.Add(frame);
            sequence = (sequence + 1) & 0x0F;
        }

        return frames;
    }

    public static byte[] Decode(IReadOnlyList<byte[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one frame is required.", nameof(frames));
        }

        foreach (var frame in frames)
        {
            if (frame is null || frame.Length is < 2 or > MaximumFrameLength)
            {
                throw new IsoTpFrameException("Each classic CAN data frame must contain 2 through 8 bytes.");
            }
        }

        var first = frames[0];
        var frameType = first[0] >> 4;
        if (frameType == 0)
        {
            var length = first[0] & 0x0F;
            if (length is < 1 or > 7 || first.Length < length + 1 || frames.Count != 1)
            {
                throw new IsoTpFrameException("Invalid single-frame length or unexpected additional frames.");
            }

            return first.AsSpan(1, length).ToArray();
        }

        if (frameType != 1 || first.Length != MaximumFrameLength)
        {
            throw new IsoTpFrameException("Expected an eight-byte first frame or a single frame.");
        }

        var payloadLength = ((first[0] & 0x0F) << 8) | first[1];
        if (payloadLength <= 7)
        {
            throw new IsoTpFrameException("First-frame length must be 8 through 4095 bytes; extended lengths are unsupported.");
        }

        var payload = new byte[payloadLength];
        first.AsSpan(2).CopyTo(payload);
        var offset = 6;
        var sequence = 1;
        for (var index = 1; index < frames.Count; index++)
        {
            var frame = frames[index];
            if (offset == payloadLength)
            {
                throw new IsoTpFrameException("Unexpected frame after payload completion.");
            }

            if ((frame[0] >> 4) != 2 || (frame[0] & 0x0F) != sequence)
            {
                throw new IsoTpFrameException($"Expected consecutive frame with sequence {sequence}.");
            }

            var count = Math.Min(7, payloadLength - offset);
            if (frame.Length < count + 1)
            {
                throw new IsoTpFrameException("Consecutive frame is shorter than its required payload.");
            }

            frame.AsSpan(1, count).CopyTo(payload.AsSpan(offset));
            offset += count;
            sequence = (sequence + 1) & 0x0F;
        }

        if (offset != payloadLength)
        {
            throw new IsoTpFrameException("Incomplete multi-frame payload.");
        }

        return payload;
    }
}

public sealed class IsoTpFrameException(string message) : FormatException(message);
