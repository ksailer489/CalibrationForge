using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalibrationForge.Core;

public enum OfflineOperation { IdentityReplay, VerifyFile, AnalyzeChecksums, KernelReplay }
public enum OperationOutcome { Running, Succeeded, Canceled, Failed }

public sealed record OperationJournalEntry(long Id, OfflineOperation Operation, DateTimeOffset StartedAt,
    TimeSpan? Duration, OperationOutcome Outcome, string? ExceptionType);

public sealed class OperationJournal
{
    public const int MaximumEntries = 100;
    private readonly object gate = new();
    private readonly List<OperationJournalEntry> entries = new();
    private long nextId;

    public IReadOnlyList<OperationJournalEntry> Snapshot()
    {
        lock (gate) { return Array.AsReadOnly(entries.ToArray()); }
    }

    public async Task<T> RunAsync<T>(OfflineOperation operation, Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(operation)) { throw new ArgumentOutOfRangeException(nameof(operation)); }
        ArgumentNullException.ThrowIfNull(action);
        OperationJournalEntry entry;
        lock (gate)
        {
            entry = new OperationJournalEntry(++nextId, operation, DateTimeOffset.UtcNow, null,
                OperationOutcome.Running, null);
            entries.Add(entry);
            if (entries.Count > MaximumEntries) { entries.RemoveAt(0); }
        }
        var started = Stopwatch.GetTimestamp();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await action(cancellationToken).ConfigureAwait(false);
            Complete(entry.Id, started, OperationOutcome.Succeeded, null);
            return result;
        }
        catch (OperationCanceledException ex)
        {
            Complete(entry.Id, started, OperationOutcome.Canceled, ex.GetType().Name);
            throw;
        }
        catch (Exception ex)
        {
            Complete(entry.Id, started, OperationOutcome.Failed, ex.GetType().Name);
            throw;
        }
    }

    public async Task ExportAsync(string newJsonPath, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(newJsonPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Support logs require a .json extension.", nameof(newJsonPath));
        }
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "CalibrationForge.SupportLog.v1",
            exportedAt = DateTimeOffset.UtcNow,
            coreVersion = typeof(OperationJournal).Assembly.GetName().Version?.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            operatingSystem = RuntimeInformation.OSDescription,
            privacy = "Operation metadata only: no file paths, VINs, controller data, payloads, or exception messages.",
            operations = Snapshot()
        }, options);
        await AtomicImageFile.SaveAsync(newJsonPath, bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void Complete(long id, long started, OperationOutcome outcome, string? exceptionType)
    {
        lock (gate)
        {
            var index = entries.FindIndex(entry => entry.Id == id);
            if (index >= 0)
            {
                entries[index] = entries[index] with
                {
                    Duration = Stopwatch.GetElapsedTime(started), Outcome = outcome, ExceptionType = exceptionType
                };
            }
        }
    }
}
