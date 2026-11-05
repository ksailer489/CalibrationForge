using System.Security.Cryptography;

namespace CalibrationForge.Core;

public sealed record MemoryRegion
{
    public MemoryRegion(uint address, int length)
    {
        if (length <= 0 || (ulong)address + (uint)length > 0x100000000UL)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Region must have a positive length within the 32-bit address space.");
        }
        Address = address;
        Length = length;
    }

    public uint Address { get; }
    public int Length { get; }
    public ulong EndExclusive => (ulong)Address + (uint)Length;
}

public sealed class MemoryReadPlan
{
    public const int MaximumImageBytes = 64 * 1024 * 1024;

    public MemoryReadPlan(uint address, int length, int chunkSize, IEnumerable<MemoryRegion>? exclusions = null)
    {
        Region = new MemoryRegion(address, length);
        if (length > MaximumImageBytes || chunkSize is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Images are bounded to 64 MiB; chunks must be 1-65535 bytes.");
        }
        var gaps = (exclusions ?? Array.Empty<MemoryRegion>()).ToArray();
        if (gaps.Any(gap => gap is null)) { throw new ArgumentException("Exclusions cannot be null.", nameof(exclusions)); }
        gaps = gaps.OrderBy(gap => gap.Address).ToArray();
        var previousEnd = (ulong)address;
        foreach (var gap in gaps)
        {
            if (gap.Address < previousEnd || gap.EndExclusive > Region.EndExclusive)
            {
                throw new ArgumentException("Exclusions must be within the image and cannot overlap.", nameof(exclusions));
            }
            previousEnd = gap.EndExclusive;
        }
        Exclusions = Array.AsReadOnly(gaps);
        ReadableBytes = length - gaps.Sum(gap => gap.Length);
        if (ReadableBytes == 0) { throw new ArgumentException("Plan must contain readable bytes.", nameof(exclusions)); }
        ChunkSize = chunkSize;
    }

    public MemoryRegion Region { get; }
    public int ChunkSize { get; }
    public int ReadableBytes { get; }
    public IReadOnlyList<MemoryRegion> Exclusions { get; }

    public IEnumerable<MemoryRegion> Chunks()
    {
        var position = (ulong)Region.Address;
        foreach (var gap in Exclusions)
        {
            foreach (var chunk in Split(position, gap.Address)) { yield return chunk; }
            position = gap.EndExclusive;
        }
        foreach (var chunk in Split(position, Region.EndExclusive)) { yield return chunk; }
    }

    private IEnumerable<MemoryRegion> Split(ulong start, ulong end)
    {
        while (start < end)
        {
            var count = (int)Math.Min((ulong)ChunkSize, end - start);
            yield return new MemoryRegion((uint)start, count);
            start += (uint)count;
        }
    }
}

public enum MemorySourceKind { File, Simulated, RecordedCanTraffic, ExperimentalReadKernel }
public sealed record MemoryReadSource(string Name, MemorySourceKind Kind, Guid SessionId);
public sealed class MemoryBlock
{
    private readonly byte[] bytes;
    public MemoryBlock(uint address, ReadOnlySpan<byte> data) { Address = address; bytes = data.ToArray(); }
    public uint Address { get; }
    public ReadOnlyMemory<byte> Data => bytes;
}

/// <summary>Own the reader exclusively; each call must perform a fresh read and report its response address.</summary>
public interface IMemoryReader
{
    MemoryReadSource Source { get; }
    int MaximumReadLength { get; }
    bool RequiresReconnect { get; }
    Task<MemoryBlock> ReadAsync(uint address, int length, CancellationToken cancellationToken);
}

public sealed class MemoryReadIntegrityException(string message) : IOException(message);
/// <summary>Only throw when a fresh retry is safe and the same reader session remains usable.</summary>
public sealed class RetryableMemoryReadException(string message) : IOException(message);
public sealed record MemoryReadProgress(int Pass, int CompletedBytes, int TotalBytes, uint Address, int Retry);

public sealed class VerifiedMemoryDump
{
    private readonly byte[] image;

    internal VerifiedMemoryDump(MemoryReadPlan plan, MemoryReadSource source, byte[] verified)
    {
        Plan = plan;
        Source = source;
        image = verified.ToArray();
        Sha256 = Convert.ToHexString(SHA256.HashData(image));
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public MemoryReadPlan Plan { get; }
    public MemoryReadSource Source { get; }
    public string Sha256 { get; }
    public DateTimeOffset CompletedAt { get; }
    public bool IsComplete => Plan.Exclusions.Count == 0;
    public byte[] CopyImage() => image.ToArray();

    public Task SaveBinAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsComplete) { throw new InvalidOperationException("Dump has unknown bytes. Export a labeled ZIP bundle, not a complete BIN."); }
        return AtomicImageFile.SaveAsync(path, image, cancellationToken: cancellationToken);
    }
}

public sealed class MemoryReadCoordinator
{
    private readonly IMemoryReader reader;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly int retries;
    private readonly TimeSpan retryDelay;

    public MemoryReadCoordinator(IMemoryReader reader, int retries = 0, TimeSpan? retryDelay = null)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.retries = retries;
        this.retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(50);
        if (retries is < 0 or > 3 || this.retryDelay < TimeSpan.Zero || this.retryDelay > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(retries), "Retries must be 0-3 with a delay of 0-1 second.");
        }
    }

    public async Task<VerifiedMemoryDump> ReadVerifiedAsync(MemoryReadPlan plan,
        IProgress<MemoryReadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = reader.Source;
            if (source is null || string.IsNullOrWhiteSpace(source.Name) || source.SessionId == Guid.Empty ||
                !Enum.IsDefined(source.Kind) || plan.ChunkSize > reader.MaximumReadLength)
            {
                throw new ArgumentException("Reader needs source/session provenance and support for the requested chunk size.");
            }
            var first = await ReadPassAsync(plan, source, 1, progress, cancellationToken).ConfigureAwait(false);
            var second = await ReadPassAsync(plan, source, 2, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckReader(source);
            for (var index = 0; index < first.Length; index++)
            {
                if ((index & 0xFFFF) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                if (first[index] != second[index])
                {
                    throw new MemoryReadIntegrityException($"Independent reads differ at address 0x{(ulong)plan.Region.Address + (uint)index:X8}. No verified dump produced.");
                }
            }
            var dump = new VerifiedMemoryDump(plan, source, first);
            cancellationToken.ThrowIfCancellationRequested();
            CheckReader(source);
            return dump;
        }
        finally { gate.Release(); }
    }

    private async Task<byte[]> ReadPassAsync(MemoryReadPlan plan, MemoryReadSource source, int pass,
        IProgress<MemoryReadProgress>? progress, CancellationToken cancellationToken)
    {
        var image = new byte[plan.Region.Length];
        Array.Fill(image, (byte)0xFF);
        var completed = 0;
        foreach (var chunk in plan.Chunks())
        {
            var attempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckReader(source);
                MemoryBlock block;
                try { block = await reader.ReadAsync(chunk.Address, chunk.Length, cancellationToken).ConfigureAwait(false); }
                catch (RetryableMemoryReadException) when (attempt < retries && !reader.RequiresReconnect)
                {
                    CheckReader(source);
                    attempt++;
                    progress?.Report(new MemoryReadProgress(pass, completed, plan.ReadableBytes, chunk.Address, attempt));
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                CheckReader(source);
                if (block is null || block.Address != chunk.Address || block.Data.Length != chunk.Length)
                {
                    throw new MemoryReadIntegrityException($"Read at 0x{chunk.Address:X8} returned a missing, misplaced, or incorrectly sized block.");
                }
                block.Data.CopyTo(image.AsMemory((int)(chunk.Address - plan.Region.Address), chunk.Length));
                completed += chunk.Length;
                progress?.Report(new MemoryReadProgress(pass, completed, plan.ReadableBytes, chunk.Address, attempt));
                break;
            }
        }
        return image;
    }

    private void CheckReader(MemoryReadSource source)
    {
        if (reader.RequiresReconnect) { throw new InvalidOperationException("Reader requires reconnection; read retries are not safe."); }
        if (reader.Source != source) { throw new MemoryReadIntegrityException("Reader identity/session changed during the dump."); }
    }
}
