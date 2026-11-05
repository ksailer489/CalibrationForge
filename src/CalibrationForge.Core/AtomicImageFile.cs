namespace CalibrationForge.Core;

public static class AtomicImageFile
{
    public static async Task SaveAsync(string path, ReadOnlyMemory<byte> image, bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = image.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            $".calibrationforge-{Guid.NewGuid():N}.tmp");
        var created = false;
        var committed = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                created = true;
                await stream.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
            committed = true;
        }
        finally
        {
            if (created && !committed) { File.Delete(temporary); }
        }
    }
}
