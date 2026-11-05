namespace CalibrationForge.Core;

public sealed class ByteCalibrationParameter
{
    public ByteCalibrationParameter(string name, int offset, byte minimum, byte maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (minimum > maximum)
        {
            throw new ArgumentException("Minimum cannot exceed maximum.", nameof(minimum));
        }

        Name = name;
        Offset = offset;
        Minimum = minimum;
        Maximum = maximum;
    }

    public string Name { get; }
    public int Offset { get; }
    public byte Minimum { get; }
    public byte Maximum { get; }
}

/// <summary>Edits unsigned-byte parameters only; no real calibration definitions are provided.</summary>
public static class ByteCalibrationEditor
{
    public static byte Read(ReadOnlySpan<byte> image, ByteCalibrationParameter parameter)
    {
        ValidateBounds(image.Length, parameter);
        return image[parameter.Offset];
    }

    public static void Write(Span<byte> image, ByteCalibrationParameter parameter, int value)
    {
        ValidateBounds(image.Length, parameter);
        if (value is < byte.MinValue or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value must fit an unsigned byte.");
        }

        if (value < parameter.Minimum || value > parameter.Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                $"Value must be between {parameter.Minimum} and {parameter.Maximum}.");
        }

        image[parameter.Offset] = (byte)value;
    }

    private static void ValidateBounds(int imageLength, ByteCalibrationParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (parameter.Offset >= imageLength)
        {
            throw new ArgumentOutOfRangeException(nameof(parameter), "Parameter offset is outside the image.");
        }
    }
}
