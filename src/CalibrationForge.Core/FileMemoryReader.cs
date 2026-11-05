namespace CalibrationForge.Core;

public sealed class FileMemoryReader : IMemoryReader, IAsyncDisposable
{
    private readonly FileStream stream;
    private bool unavailable;

    public FileMemoryReader(string path)
    {
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length is < 1 or > MemoryReadPlan.MaximumImageBytes)
        {
            stream.Dispose();
            throw new ArgumentException("Source files must contain 1 byte through 64 MiB.", nameof(path));
        }
        Length = (int)stream.Length;
        Source = new MemoryReadSource(Path.GetFileName(path), MemorySourceKind.File, Guid.NewGuid());
    }

    public int Length { get; }
    public MemoryReadSource Source { get; }
    public int MaximumReadLength => 65535;
    public bool RequiresReconnect => unavailable;

    public async Task<MemoryBlock> ReadAsync(uint address, int length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (unavailable) { throw new InvalidOperationException("File reader is unavailable. Open a fresh reader."); }
        if (length is < 1 or > 65535 || (ulong)address + (uint)length > (uint)Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Read must be within the source file.");
        }
        var completed = false;
        try
        {
            stream.Position = address;
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            completed = true;
            return new MemoryBlock(address, bytes);
        }
        finally { if (!completed) { unavailable = true; } }
    }

    public async ValueTask DisposeAsync()
    {
        unavailable = true;
        await stream.DisposeAsync().ConfigureAwait(false);
    }
}
