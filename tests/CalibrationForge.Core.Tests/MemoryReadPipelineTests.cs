using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CalibrationForge.Core;

namespace CalibrationForge.Core.Tests;

public sealed class MemoryReadPipelineTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"calibrationforge-read-{Guid.NewGuid():N}");
    public MemoryReadPipelineTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public async Task CoordinatorRequestsExactChunksTwiceIncludingShortTail()
    {
        var reader = new ScriptedReader();
        var progress = new CollectedProgress();
        var dump = await new MemoryReadCoordinator(reader).ReadVerifiedAsync(new MemoryReadPlan(0x100, 10, 4), progress);

        Assert.Equal(new uint[] { 0x100, 0x104, 0x108, 0x100, 0x104, 0x108 }, reader.Requests.Select(item => item.Address));
        Assert.Equal(new[] { 4, 4, 2, 4, 4, 2 }, reader.Requests.Select(item => item.Length));
        Assert.Equal(Enumerable.Range(0, 10).Select(value => (byte)value), dump.CopyImage());
        Assert.True(dump.IsComplete);
        Assert.Equal(2, progress.Items[^1].Pass);
        Assert.Equal(10, progress.Items[^1].CompletedBytes);
    }

    [Theory]
    [InlineData(ReadFault.WrongAddress)]
    [InlineData(ReadFault.ShortBlock)]
    [InlineData(ReadFault.NullBlock)]
    public async Task MissingOrMisplacedDataIsNotFilledOrRetried(ReadFault fault)
    {
        var reader = new ScriptedReader { Fault = fault, FaultAt = 2 };

        await Assert.ThrowsAsync<MemoryReadIntegrityException>(() =>
            new MemoryReadCoordinator(reader, retries: 3).ReadVerifiedAsync(new MemoryReadPlan(0x100, 12, 4)));

        Assert.Equal(2, reader.Requests.Count);
    }

    [Fact]
    public async Task IndependentReadMismatchProducesNoVerifiedDump()
    {
        var reader = new ScriptedReader { Fault = ReadFault.DifferentData, FaultAt = 4 };

        await Assert.ThrowsAsync<MemoryReadIntegrityException>(() =>
            new MemoryReadCoordinator(reader).ReadVerifiedAsync(new MemoryReadPlan(0x100, 12, 4)));

        Assert.Equal(6, reader.Requests.Count);
    }

    [Fact]
    public async Task ChangingReaderSessionInvalidatesTheDump()
    {
        var reader = new ScriptedReader { Fault = ReadFault.NewSession, FaultAt = 2 };
        await Assert.ThrowsAsync<MemoryReadIntegrityException>(() =>
            new MemoryReadCoordinator(reader).ReadVerifiedAsync(new MemoryReadPlan(0x100, 8, 4)));
    }

    [Fact]
    public async Task ExplicitlyRetryableFailureRequiresOptInAndReportsRetry()
    {
        var reader = new ScriptedReader { Fault = ReadFault.Retryable, FaultAt = 1 };
        await Assert.ThrowsAsync<RetryableMemoryReadException>(() =>
            new MemoryReadCoordinator(reader).ReadVerifiedAsync(new MemoryReadPlan(0x100, 8, 4)));

        reader = new ScriptedReader { Fault = ReadFault.Retryable, FaultAt = 1 };
        var progress = new CollectedProgress();
        var dump = await new MemoryReadCoordinator(reader, 1, TimeSpan.Zero)
            .ReadVerifiedAsync(new MemoryReadPlan(0x100, 8, 4), progress);

        Assert.Equal(5, reader.Requests.Count);
        Assert.Equal(8, dump.CopyImage().Length);
        Assert.Contains(progress.Items, item => item.Retry == 1 && item.CompletedBytes == 0);
    }

    [Fact]
    public async Task RetryableLabelCannotOverrideRequiredReconnection()
    {
        var reader = new ScriptedReader { Fault = ReadFault.RetryNeedsReconnect, FaultAt = 1 };
        await Assert.ThrowsAsync<RetryableMemoryReadException>(() =>
            new MemoryReadCoordinator(reader, retries: 3).ReadVerifiedAsync(new MemoryReadPlan(0x100, 8, 4)));
        Assert.Single(reader.Requests);
    }

    [Fact]
    public async Task SafeRetriesAreBoundedRatherThanLoopingIndefinitely()
    {
        var reader = new ScriptedReader { Fault = ReadFault.Retryable, FaultAt = 1, RepeatFault = true };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<RetryableMemoryReadException>(() =>
            new MemoryReadCoordinator(reader, 3, TimeSpan.FromMilliseconds(1))
                .ReadVerifiedAsync(new MemoryReadPlan(0x100, 8, 4), cancellationToken: cancellation.Token));
        Assert.Equal(4, reader.Requests.Count);
    }

    [Fact]
    public async Task CancelingAQueuedDumpDoesNotInterruptTheActiveDump()
    {
        var reader = new GatedReader();
        var coordinator = new MemoryReadCoordinator(reader);
        var active = coordinator.ReadVerifiedAsync(new MemoryReadPlan(0x100, 8, 4));
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        try
        {
            var queued = coordinator.ReadVerifiedAsync(new MemoryReadPlan(0x100, 8, 4), cancellationToken: cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(1, reader.Calls);
        }
        finally { reader.Resume.TrySetResult(); }
        Assert.True((await active.WaitAsync(TimeSpan.FromSeconds(2))).IsComplete);
        Assert.Equal(4, reader.Calls);
    }

    [Fact]
    public async Task TimeoutDoesNotAutomaticallyRetry()
    {
        var reader = new ScriptedReader { Fault = ReadFault.Timeout, FaultAt = 1 };
        await Assert.ThrowsAsync<TimeoutException>(() =>
            new MemoryReadCoordinator(reader, retries: 3).ReadVerifiedAsync(new MemoryReadPlan(0x100, 8, 4)));
        Assert.Single(reader.Requests);
    }

    [Fact]
    public async Task CancellationAfterAChunkDoesNotFinishOrExport()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new ScriptedReader { CancelAt = 2, Cancellation = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MemoryReadCoordinator(reader).ReadVerifiedAsync(new MemoryReadPlan(0x100, 12, 4), cancellationToken: cancellation.Token));
        Assert.Equal(2, reader.Requests.Count);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task KnownExclusionsAreNotReadAndCannotBeExportedAsCompleteBin()
    {
        var reader = new ScriptedReader();
        var plan = new MemoryReadPlan(0x100, 12, 4, new[] { new MemoryRegion(0x104, 4) });
        var dump = await new MemoryReadCoordinator(reader).ReadVerifiedAsync(plan);

        Assert.Equal(new uint[] { 0x100, 0x108, 0x100, 0x108 }, reader.Requests.Select(item => item.Address));
        Assert.False(dump.IsComplete);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, dump.CopyImage()[4..8]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dump.SaveBinAsync(Path.Combine(directory, "unsafe.bin")));
        var path = Path.Combine(directory, "partial.zip");
        await MemoryDumpFile.SaveBundleAsync(path, dump);
        using var zip = ZipFile.OpenRead(path);
        Assert.Null(zip.GetEntry("image.bin"));
        Assert.NotNull(zip.GetEntry("image.partial.bin"));
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        Assert.False(manifest.RootElement.GetProperty("completeRequestedRange").GetBoolean());
        Assert.Equal("0x00000104", manifest.RootElement.GetProperty("excludedRanges")[0].GetProperty("address").GetString());
        Assert.Contains("unknown", manifest.RootElement.GetProperty("excludedRanges")[0].GetProperty("placeholder").GetString());
    }

    [Fact]
    public async Task ActualFileReadsAndAtomicBundlePreserveOriginalAndRefuseOverwrite()
    {
        var original = Enumerable.Range(0, 513).Select(value => (byte)value).ToArray();
        var source = Path.Combine(directory, "source.bin");
        var output = Path.Combine(directory, "dump.zip");
        await File.WriteAllBytesAsync(source, original);
        await using var reader = new FileMemoryReader(source);
        var dump = await new MemoryReadCoordinator(reader).ReadVerifiedAsync(new MemoryReadPlan(0, reader.Length, 128));
        await MemoryDumpFile.SaveBundleAsync(output, dump);
        var bundle = await File.ReadAllBytesAsync(output);

        using (var zip = ZipFile.OpenRead(output))
        {
            using var image = new MemoryStream();
            await zip.GetEntry("image.bin")!.Open().CopyToAsync(image);
            Assert.Equal(original, image.ToArray());
            using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), manifest.RootElement.GetProperty("sha256").GetString());
            Assert.Equal(2, manifest.RootElement.GetProperty("independentReadPasses").GetInt32());
        }
        await Assert.ThrowsAsync<IOException>(() => MemoryDumpFile.SaveBundleAsync(output, dump));
        Assert.Equal(bundle, await File.ReadAllBytesAsync(output));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public async Task CompleteBinExportCopiesVerifiedBytesAndDoesNotExposeMutableBacking()
    {
        var dump = await new MemoryReadCoordinator(new ScriptedReader()).ReadVerifiedAsync(new MemoryReadPlan(0x100, 8, 4));
        var copy = dump.CopyImage();
        copy[0] = 0xFF;
        var path = Path.Combine(directory, "copy.bin");
        await dump.SaveBinAsync(path);
        Assert.Equal(Enumerable.Range(0, 8).Select(value => (byte)value), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task CanceledBundleExportCreatesNoOutputOrTemporaryFile()
    {
        var dump = await new MemoryReadCoordinator(new ScriptedReader()).ReadVerifiedAsync(new MemoryReadPlan(0x100, 8, 4));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MemoryDumpFile.SaveBundleAsync(Path.Combine(directory, "canceled.zip"), dump, cancellation.Token));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task OversizedReaderRequestsAreRejectedBeforeAnyIo()
    {
        var reader = new ScriptedReader();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new MemoryReadCoordinator(reader).ReadVerifiedAsync(new MemoryReadPlan(0x100, 12, 5)));
        Assert.Empty(reader.Requests);
    }

    [Fact]
    public void InvalidPlansAndAddressWrapAreRejectedWithoutAllocatingImages()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryRegion(uint.MaxValue, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryReadPlan(0, MemoryReadPlan.MaximumImageBytes + 1, 4));
        Assert.Throws<ArgumentException>(() => new MemoryReadPlan(0x100, 12, 4, new[] { new MemoryRegion(0xFF, 2) }));
        Assert.Throws<ArgumentException>(() => new MemoryReadPlan(0x100, 12, 4, new[] { new MemoryRegion(0x102, 4), new MemoryRegion(0x104, 4) }));
        Assert.Throws<ArgumentException>(() => new MemoryReadPlan(0x100, 8, 4, new[] { new MemoryRegion(0x100, 8) }));
        Assert.Single(new MemoryReadPlan(uint.MaxValue, 1, 1).Chunks());
    }

    public enum ReadFault { None, WrongAddress, ShortBlock, NullBlock, DifferentData, NewSession, Retryable, RetryNeedsReconnect, Timeout }

    private sealed class ScriptedReader : IMemoryReader
    {
        public MemoryReadSource Source { get; private set; } = new("Synthetic chunk reader", MemorySourceKind.Simulated, Guid.NewGuid());
        public int MaximumReadLength => 4;
        public bool RequiresReconnect { get; private set; }
        public List<MemoryRegion> Requests { get; } = new();
        public ReadFault Fault { get; init; }
        public int FaultAt { get; init; }
        public bool RepeatFault { get; init; }
        public int CancelAt { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }

        public Task<MemoryBlock> ReadAsync(uint address, int length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new MemoryRegion(address, length));
            if (Requests.Count == CancelAt) { Cancellation!.Cancel(); }
            var bytes = Enumerable.Range(0, length).Select(value => (byte)(address + (uint)value)).ToArray();
            if (Requests.Count == FaultAt || (RepeatFault && Requests.Count >= FaultAt))
            {
                switch (Fault)
                {
                    case ReadFault.WrongAddress: address += 4; break;
                    case ReadFault.ShortBlock: bytes = bytes[..^1]; break;
                    case ReadFault.NullBlock: return Task.FromResult<MemoryBlock>(null!);
                    case ReadFault.DifferentData: bytes[0] ^= 1; break;
                    case ReadFault.NewSession: Source = Source with { SessionId = Guid.NewGuid() }; break;
                    case ReadFault.Retryable: throw new RetryableMemoryReadException("Scripted read-only retry.");
                    case ReadFault.RetryNeedsReconnect:
                        RequiresReconnect = true;
                        throw new RetryableMemoryReadException("Session no longer usable.");
                    case ReadFault.Timeout: throw new TimeoutException("Scripted timeout.");
                }
            }
            return Task.FromResult(new MemoryBlock(address, bytes));
        }
    }

    private sealed class CollectedProgress : IProgress<MemoryReadProgress>
    {
        public List<MemoryReadProgress> Items { get; } = new();
        public void Report(MemoryReadProgress value) => Items.Add(value);
    }

    private sealed class GatedReader : IMemoryReader
    {
        public MemoryReadSource Source { get; } = new("Synthetic gated reader", MemorySourceKind.Simulated, Guid.NewGuid());
        public int MaximumReadLength => 4;
        public bool RequiresReconnect => false;
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MemoryBlock> ReadAsync(uint address, int length, CancellationToken cancellationToken)
        {
            if (++Calls == 1)
            {
                Entered.SetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return new MemoryBlock(address, Enumerable.Range(0, length).Select(value => (byte)(address + (uint)value)).ToArray());
        }
    }
}
