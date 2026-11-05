using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CalibrationForge.Desktop.SmokeTests")]

namespace CalibrationForge.Core;

public sealed record IdentityReplayResult(string Source, bool IsSimulation, ObdIdentity Identity);

public interface IOfflineWorkflowService
{
    OperationJournal Journal { get; }
    Task<IdentityReplayResult> ReadIdentityAsync(string? tracePath = null, CancellationToken cancellationToken = default);
    Task<VerifiedMemoryDump> VerifyFileAsync(string sourcePath, string newZipPath,
        IProgress<MemoryReadProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<AdditiveChecksumReport> AnalyzeChecksumsAsync(string referencePath, string? candidatePath = null,
        CancellationToken cancellationToken = default);
}

public sealed class OfflineWorkflowService(OperationJournal? journal = null) : IOfflineWorkflowService
{
    public OperationJournal Journal { get; } = journal ?? new OperationJournal();

    public Task<IdentityReplayResult> ReadIdentityAsync(string? tracePath = null, CancellationToken cancellationToken = default) =>
        Journal.RunAsync(OfflineOperation.IdentityReplay, token => Task.Run(async () =>
        {
            var trace = tracePath is null ? CanTraceReplay.Parse(ObdIdentityExample.Json)
                : await CanTraceReplay.LoadAsync(tracePath, token).ConfigureAwait(false);
            var identity = await new ObdIdentityClient(new IsoTpSession(trace)).ReadAsync(token).ConfigureAwait(false);
            trace.AssertComplete();
            token.ThrowIfCancellationRequested();
            return new IdentityReplayResult(trace.Source, trace.IsSimulation, identity);
        }, token), cancellationToken);

    public Task<VerifiedMemoryDump> VerifyFileAsync(string sourcePath, string newZipPath,
        IProgress<MemoryReadProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Journal.RunAsync(OfflineOperation.VerifyFile, token => Task.Run(async () =>
        {
            await using var reader = new FileMemoryReader(sourcePath);
            var dump = await new MemoryReadCoordinator(reader).ReadVerifiedAsync(
                new MemoryReadPlan(0, reader.Length, 2048), progress, token).ConfigureAwait(false);
            await MemoryDumpFile.SaveBundleAsync(newZipPath, dump, token).ConfigureAwait(false);
            return dump;
        }, token), cancellationToken);

    public Task<AdditiveChecksumReport> AnalyzeChecksumsAsync(string referencePath, string? candidatePath = null,
        CancellationToken cancellationToken = default) =>
        Journal.RunAsync(OfflineOperation.AnalyzeChecksums, token => Task.Run(async () =>
        {
            var reference = await ReadBoundedAsync(referencePath, token).ConfigureAwait(false);
            var candidate = candidatePath is null ? reference : await ReadBoundedAsync(candidatePath, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var report = AdditiveChecksumAnalysis.Analyze(reference, candidate);
            token.ThrowIfCancellationRequested();
            return report;
        }, token), cancellationToken);

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length is < 1 or > MemoryReadPlan.MaximumImageBytes)
        {
            throw new FormatException("Images must contain 1 byte through 64 MiB.");
        }
        var image = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(image, token).ConfigureAwait(false);
        return image;
    }
}
