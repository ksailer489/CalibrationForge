namespace CalibrationForge.Core;

public static class SyntheticImageFile
{
    public static async Task<byte[]> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length != SyntheticCalibration.ImageLength)
        {
            throw new ArgumentException($"Select a raw synthetic image of exactly {SyntheticCalibration.ImageLength} bytes, not a genuine E92 dump.");
        }

        var candidate = new byte[SyntheticCalibration.ImageLength];
        await stream.ReadExactlyAsync(candidate, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return SyntheticCalibration.CopyValidatedImage(candidate);
    }

    public static Task SaveAsync(string path, ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
    {
        var snapshot = SyntheticCalibration.CopyValidatedImage(image.Span);
        return AtomicImageFile.SaveAsync(path, snapshot, overwrite: true, cancellationToken);
    }
}
