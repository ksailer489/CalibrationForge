namespace CalibrationForge.Core;

public static class SyntheticCalibration
{
    public const int ImageLength = 16;

    public static ByteCalibrationParameter DemoLevel { get; } = new("DemoLevel", 4, 0, 100);

    public static byte[] CopyValidatedImage(ReadOnlySpan<byte> candidate)
    {
        if (candidate.Length != ImageLength)
        {
            throw new ArgumentException($"Raw synthetic images must contain exactly {ImageLength} bytes. Genuine E92 dumps are not supported.");
        }

        var validated = candidate.ToArray();
        var value = ByteCalibrationEditor.Read(validated, DemoLevel);
        ByteCalibrationEditor.Write(validated, DemoLevel, value);
        return validated;
    }
}

/// <summary>In-memory synthetic device, unrelated to E92 image layouts or hardware.</summary>
public sealed class SimulatedCalibrationDevice : ICalibrationDevice
{
    private readonly object gate = new();
    private byte[] image = new byte[SyntheticCalibration.ImageLength];

    public SimulatedCalibrationDevice()
    {
        ByteCalibrationEditor.Write(image, SyntheticCalibration.DemoLevel, 25);
    }

    public Task<byte[]> ReadImageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult((byte[])image.Clone());
        }
    }

    public Task WriteImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (image.Length != SyntheticCalibration.ImageLength)
        {
            throw new ArgumentException($"Synthetic image must contain {SyntheticCalibration.ImageLength} bytes.", nameof(image));
        }

        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.image = image.ToArray();
        }

        return Task.CompletedTask;
    }
}
