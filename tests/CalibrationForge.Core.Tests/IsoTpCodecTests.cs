using CalibrationForge.Core;

namespace CalibrationForge.Core.Tests;

public class IsoTpCodecTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void SingleFrameUsesLengthNibbleAndRoundTrips(int length)
    {
        var payload = Payload(length);
        var frame = Assert.Single(IsoTpCodec.Encode(payload));

        Assert.Equal((byte)length, frame[0]);
        Assert.Equal(payload, frame.AsSpan(1).ToArray());
        Assert.Equal(payload, IsoTpCodec.Decode(new[] { frame }));
    }

    [Fact]
    public void MultiFrameUsesFirstAndConsecutiveHeaders()
    {
        var payload = Payload(14);
        var frames = IsoTpCodec.Encode(payload);

        Assert.Equal(new byte[] { 0x10, 14, 0, 1, 2, 3, 4, 5 }, frames[0]);
        Assert.Equal(new byte[] { 0x21, 6, 7, 8, 9, 10, 11, 12 }, frames[1]);
        Assert.Equal(new byte[] { 0x22, 13 }, frames[2]);
        Assert.Equal(payload, IsoTpCodec.Decode(frames));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(118)]
    [InlineData(4095)]
    public void MultiFrameRoundTripsWithinClassicCanAndTwelveBitLimits(int length)
    {
        var payload = Payload(length);
        var frames = IsoTpCodec.Encode(payload);

        Assert.All(frames, frame => Assert.InRange(frame.Length, 2, 8));
        Assert.Equal(length, ((frames[0][0] & 0x0F) << 8) | frames[0][1]);
        Assert.Equal(payload, IsoTpCodec.Decode(frames));
    }

    [Fact]
    public void SequenceWrapsFromFifteenToZeroThenOne()
    {
        var frames = IsoTpCodec.Encode(Payload(125));

        Assert.Equal(0x2F, frames[15][0]);
        Assert.Equal(0x20, frames[16][0]);
        Assert.Equal(0x21, frames[17][0]);
        Assert.Equal(Payload(125), IsoTpCodec.Decode(frames));
    }

    [Fact]
    public void DecodeAcceptsPaddingWithoutIncludingItInPayload()
    {
        Assert.Equal(new byte[] { 42 }, IsoTpCodec.Decode(new[]
        {
            new byte[] { 0x01, 42, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }
        }));
        Assert.Equal(Payload(8), IsoTpCodec.Decode(new[]
        {
            new byte[] { 0x10, 8, 0, 1, 2, 3, 4, 5 },
            new byte[] { 0x21, 6, 7, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }
        }));
    }

    [Fact]
    public void DecodeHandlesSequenceWrapIndependentlyOfEncoder()
    {
        var frames = new List<byte[]> { new byte[] { 0x10, 118, 0, 0, 0, 0, 0, 0 } };
        for (var sequence = 1; sequence <= 16; sequence++)
        {
            frames.Add(new byte[] { (byte)(0x20 | (sequence & 0x0F)), 0, 0, 0, 0, 0, 0, 0 });
        }

        Assert.Equal(new byte[118], IsoTpCodec.Decode(frames));
        frames[16][0] = 0x21;
        Assert.Throws<IsoTpFrameException>(() => IsoTpCodec.Decode(frames));
    }

    public static IEnumerable<object[]> InvalidFrames()
    {
        yield return new object[] { new byte[][] { null! } };
        yield return new object[] { new[] { Array.Empty<byte>() } };
        yield return new object[] { new[] { new byte[] { 1 } } };
        yield return new object[] { new[] { new byte[9] } };
        yield return new object[] { new[] { new byte[] { 0, 0 } } };
        yield return new object[] { new[] { new byte[] { 8, 0, 0, 0, 0, 0, 0, 0 } } };
        yield return new object[] { new[] { new byte[] { 7, 0 } } };
        yield return new object[] { new[] { new byte[] { 0x10, 8, 0 } } };
        yield return new object[] { new[] { new byte[] { 0x10, 0, 0, 0, 0, 0, 0, 0 } } };
        yield return new object[] { new[] { new byte[] { 0x10, 7, 0, 0, 0, 0, 0, 0 } } };
        yield return new object[] { new[] { new byte[] { 0x10, 8, 0, 0, 0, 0, 0, 0 } } };
        yield return new object[] { new[] { new byte[] { 0x21, 0 } } };
        yield return new object[] { new[] { new byte[] { 0x30, 0, 0 } } };
        yield return new object[] { new[] { new byte[] { 0x40, 0 } } };
        yield return new object[] { new[] { new byte[] { 1, 0 }, new byte[] { 0x21, 0 } } };
        yield return new object[] { new[]
        {
            new byte[] { 0x10, 8, 0, 0, 0, 0, 0, 0 }, new byte[] { 0x21, 0 }
        } };
        yield return new object[] { new[]
        {
            new byte[] { 0x10, 20, 0, 0, 0, 0, 0, 0 }, new byte[] { 0x21, 0, 0 }
        } };
        yield return new object[] { new[]
        {
            new byte[] { 0x10, 8, 0, 0, 0, 0, 0, 0 }, new byte[] { 0x22, 0, 0 }
        } };
        yield return new object[] { new[]
        {
            new byte[] { 0x10, 8, 0, 0, 0, 0, 0, 0 }, new byte[] { 0x30, 0, 0 }
        } };
        yield return new object[] { new[]
        {
            new byte[] { 0x10, 8, 0, 0, 0, 0, 0, 0 },
            new byte[] { 0x21, 0, 0 }, new byte[] { 0x22, 0 }
        } };
    }

    [Theory]
    [MemberData(nameof(InvalidFrames))]
    public void MalformedOrOutOfOrderFramesFailExplicitly(byte[][] frames)
    {
        Assert.Throws<IsoTpFrameException>(() => IsoTpCodec.Decode(frames));
    }

    [Fact]
    public void SwappedOrRepeatedConsecutiveFramesAreRejected()
    {
        var frames = IsoTpCodec.Encode(Payload(20)).ToArray();
        (frames[1], frames[2]) = (frames[2], frames[1]);
        Assert.Throws<IsoTpFrameException>(() => IsoTpCodec.Decode(frames));

        frames = IsoTpCodec.Encode(Payload(20)).ToArray();
        frames[2][0] = frames[1][0];
        Assert.Throws<IsoTpFrameException>(() => IsoTpCodec.Decode(frames));
    }

    [Fact]
    public void ArgumentsAreValidatedEagerly()
    {
        Assert.Throws<ArgumentNullException>(() => IsoTpCodec.Encode(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => IsoTpCodec.Encode(Array.Empty<byte>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => IsoTpCodec.Encode(new byte[4096]));
        Assert.Throws<ArgumentNullException>(() => IsoTpCodec.Decode(null!));
        Assert.Throws<ArgumentException>(() => IsoTpCodec.Decode(Array.Empty<byte[]>()));
    }

    private static byte[] Payload(int length) => Enumerable.Range(0, length).Select(index => (byte)index).ToArray();
}
