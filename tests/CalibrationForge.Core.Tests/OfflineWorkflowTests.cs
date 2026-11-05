using System.Text.Json;
using CalibrationForge.Core;
using CalibrationForge.Desktop;

namespace CalibrationForge.Core.Tests;

public sealed class OfflineWorkflowTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "workflow-tests-" + Guid.NewGuid().ToString("N"));

    public OfflineWorkflowTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string PathFor(string name) => Path.Combine(directory, name);

    [Fact]
    public async Task IdentityDemoAndImportedSimulationRemainLabeledAndMalformedReplayFails()
    {
        var service = new OfflineWorkflowService();
        var demo = await service.ReadIdentityAsync();
        Assert.True(demo.IsSimulation);
        Assert.NotEmpty(demo.Source);
        var path = PathFor("trace.json");
        await File.WriteAllTextAsync(path, ObdIdentityExample.Json);
        Assert.True((await service.ReadIdentityAsync(path)).IsSimulation);
        await File.WriteAllTextAsync(path, "{}");
        await Assert.ThrowsAnyAsync<JsonException>(() => service.ReadIdentityAsync(path));
        Assert.Equal(OperationOutcome.Failed, service.Journal.Snapshot().Last().Outcome);
    }

    [Fact]
    public async Task VerifyExportsAtomicBundlePreservesOriginalAndRefusesExistingDestination()
    {
        var original = Enumerable.Range(0, 10000).Select(index => (byte)index).ToArray();
        var source = PathFor("source.bin");
        var output = PathFor("verified.zip");
        await File.WriteAllBytesAsync(source, original);
        var service = new OfflineWorkflowService();
        var dump = await service.VerifyFileAsync(source, output);
        Assert.Equal(MemorySourceKind.File, dump.Source.Kind);
        Assert.Equal(original, dump.CopyImage());
        Assert.True(dump.IsComplete);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(output))
        {
            Assert.Contains(zip.Entries, entry => entry.FullName.EndsWith(".json", StringComparison.Ordinal));
            Assert.Contains(zip.Entries, entry => entry.FullName.EndsWith(".bin", StringComparison.Ordinal));
        }
        var bundle = await File.ReadAllBytesAsync(output);
        await Assert.ThrowsAnyAsync<IOException>(() => service.VerifyFileAsync(source, output));
        Assert.Equal(bundle, await File.ReadAllBytesAsync(output));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Single(Directory.GetFiles(directory, "*.zip"));
    }

    [Fact]
    public async Task CancellationProducesNoBundleAndChecksumRequiresExactSupportedReference()
    {
        var source = PathFor("unknown.bin");
        await File.WriteAllBytesAsync(source, new byte[4096]);
        var service = new OfflineWorkflowService();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.VerifyFileAsync(source, PathFor("canceled.zip"), cancellationToken: canceled.Token));
        Assert.False(File.Exists(PathFor("canceled.zip")));
        using var duringRead = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.VerifyFileAsync(source, PathFor("interrupted.zip"),
                new InlineProgress(_ => duringRead.Cancel()), duringRead.Token));
        Assert.False(File.Exists(PathFor("interrupted.zip")));
        var exactSizeUnknown = PathFor("wrong-hash.bin");
        await File.WriteAllBytesAsync(exactSizeUnknown, new byte[ExperimentalBinProfile.E92Sample.Size]);
        await Assert.ThrowsAsync<FormatException>(() => service.AnalyzeChecksumsAsync(exactSizeUnknown));
        await using (var large = new FileStream(PathFor("large.bin"), FileMode.CreateNew))
        {
            large.SetLength((long)MemoryReadPlan.MaximumImageBytes + 1);
        }
        await Assert.ThrowsAsync<FormatException>(() => service.AnalyzeChecksumsAsync(PathFor("large.bin")));
        Assert.Equal(new byte[4096], await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task ToolsBusyCancelAndFailureNeverExposePriorSuccessAsNewResult()
    {
        var service = new BlockingWorkflow();
        var model = new ToolsViewModel(service);
        await model.IdentityAsync();
        Assert.Contains("SIMULATED", model.Provenance);
        Assert.Contains("Reported VIN", model.Result);
        service.BlockIdentity = true;
        var pending = model.IdentityAsync();
        Assert.True(model.IsBusy);
        Assert.False(model.CanStart);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.IdentityAsync());
        model.Cancel();
        await pending;
        Assert.False(model.IsBusy);
        Assert.True(model.CanStart);
        Assert.Empty(model.Result);
        Assert.Contains("CANCELED", model.Provenance);
        await model.AnalyzeAsync(PathFor("missing.bin"), null);
        Assert.Empty(model.Result);
        Assert.Contains("FAILED", model.Provenance);
        Assert.Contains("originals preserved", model.Status);
        service.BlockIdentity = false;
        await model.IdentityAsync();
        Assert.Contains("SIMULATED", model.Provenance);
    }

    [Fact]
    public async Task JournalRecordsRunningAndTerminalStatesWithoutSensitiveMessagesAndBoundsHistory()
    {
        var journal = new OperationJournal();
        var release = new TaskCompletionSource<int>();
        var pending = journal.RunAsync(OfflineOperation.IdentityReplay, _ => release.Task);
        Assert.Equal(OperationOutcome.Running, Assert.Single(journal.Snapshot()).Outcome);
        release.SetResult(3);
        Assert.Equal(3, await pending);
        Assert.Equal(OperationOutcome.Succeeded, journal.Snapshot()[0].Outcome);
        Assert.NotNull(journal.Snapshot()[0].Duration);
        var error = new IOException("PRIVATE-VIN-AND-PATH");
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() =>
            journal.RunAsync<int>(OfflineOperation.VerifyFile, _ => throw error)));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            journal.RunAsync(OfflineOperation.AnalyzeChecksums, _ => Task.FromResult(1), canceled.Token));
        Assert.Equal(OperationOutcome.Canceled, journal.Snapshot()[2].Outcome);
        var output = PathFor("support.json");
        await journal.ExportAsync(output);
        var contents = await File.ReadAllTextAsync(output);
        Assert.DoesNotContain("PRIVATE-VIN-AND-PATH", contents);
        using var json = JsonDocument.Parse(contents);
        Assert.Equal("IOException", json.RootElement.GetProperty("operations")[1].GetProperty("exceptionType").GetString());
        await Assert.ThrowsAnyAsync<IOException>(() => journal.ExportAsync(output));
        Assert.Equal(contents, await File.ReadAllTextAsync(output));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.ExportAsync(PathFor("canceled.json"), canceled.Token));
        Assert.False(File.Exists(PathFor("canceled.json")));
        for (var index = 0; index < 110; index++)
        {
            await journal.RunAsync(OfflineOperation.IdentityReplay, _ => Task.FromResult(0));
        }
        Assert.Equal(OperationJournal.MaximumEntries, journal.Snapshot().Count);
        Assert.Equal(14L, journal.Snapshot()[0].Id);
        Assert.All(journal.Snapshot(), entry => Assert.Equal(OperationOutcome.Succeeded, entry.Outcome));
    }

    private sealed class InlineProgress(Action<MemoryReadProgress> report) : IProgress<MemoryReadProgress>
    {
        public void Report(MemoryReadProgress value) => report(value);
    }

    private sealed class BlockingWorkflow : IOfflineWorkflowService
    {
        private readonly OfflineWorkflowService inner = new();
        public bool BlockIdentity { get; set; }
        public OperationJournal Journal => inner.Journal;
        public async Task<IdentityReplayResult> ReadIdentityAsync(string? tracePath = null, CancellationToken cancellationToken = default)
        {
            if (BlockIdentity) { await Task.Delay(Timeout.Infinite, cancellationToken); }
            return await inner.ReadIdentityAsync(tracePath, cancellationToken);
        }
        public Task<VerifiedMemoryDump> VerifyFileAsync(string sourcePath, string newZipPath,
            IProgress<MemoryReadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            inner.VerifyFileAsync(sourcePath, newZipPath, progress, cancellationToken);
        public Task<AdditiveChecksumReport> AnalyzeChecksumsAsync(string referencePath, string? candidatePath = null,
            CancellationToken cancellationToken = default) =>
            inner.AnalyzeChecksumsAsync(referencePath, candidatePath, cancellationToken);
    }
}
