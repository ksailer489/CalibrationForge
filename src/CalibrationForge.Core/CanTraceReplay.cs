using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalibrationForge.Core;

public sealed class CanTraceReplay : ICanTransport
{
    private const int MaximumBytes = 1024 * 1024;
    private readonly IReadOnlyList<(bool Transmit, CanFrame Frame)> frames;
    private int index;

    private CanTraceReplay(string source, bool isSimulation, List<(bool, CanFrame)> frames)
    {
        Source = source;
        IsSimulation = isSimulation;
        this.frames = frames.AsReadOnly();
    }

    public string Source { get; }
    public bool IsSimulation { get; }
    public int RemainingFrames => frames.Count - index;

    public static CanTraceReplay Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) { throw new FormatException("CAN traces must be at most 1 MiB."); }
        var trace = JsonSerializer.Deserialize<TraceData>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new FormatException("CAN trace must be a JSON object.");
        if (trace.Format != "CalibrationForge.CanTrace.v1" || string.IsNullOrWhiteSpace(trace.Source) ||
            trace.Source.Length > 512 || trace.Frames is null || trace.Frames.Count > 10000)
        {
            throw new FormatException("Trace requires format CalibrationForge.CanTrace.v1, a source, and at most 10000 frames.");
        }
        var frames = new List<(bool, CanFrame)>();
        foreach (var entry in trace.Frames)
        {
            if (entry is null || entry.Direction is not ("tx" or "rx") || entry.Data is null ||
                !uint.TryParse(entry.Id, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) || id > 0x7FF)
            {
                throw new FormatException("Trace frames require tx/rx direction, a standard CAN ID, and hexadecimal data.");
            }
            var hex = string.Concat(entry.Data.Where(character => !char.IsWhiteSpace(character)));
            if (hex.Length > 16) { throw new FormatException("Trace supports only classic CAN frames of at most eight bytes."); }
            frames.Add((entry.Direction == "tx", new CanFrame(id, Convert.FromHexString(hex))));
        }
        return new CanTraceReplay(trace.Source, trace.IsSimulation, frames);
    }

    public static async Task<CanTraceReplay> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length is < 2 or > MaximumBytes) { throw new FormatException("CAN trace file must contain JSON and be at most 1 MiB."); }
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(json);
    }

    public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        var expected = Next();
        if (!expected.Transmit || frame.Id != expected.Frame.Id || !frame.Data.Span.SequenceEqual(expected.Frame.Data.Span))
        {
            throw new FormatException($"Transmit does not match recorded frame {index + 1}; replay is not verified.");
        }
        index++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<CanFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expected = Next();
        if (expected.Transmit) { throw new FormatException($"Recorded frame {index + 1} requires a transmit before receiving."); }
        index++;
        return ValueTask.FromResult(expected.Frame);
    }

    public void AssertComplete()
    {
        if (RemainingFrames != 0) { throw new FormatException($"Replay left {RemainingFrames} unconsumed frames; the full trace was not verified."); }
    }

    private (bool Transmit, CanFrame Frame) Next() => index < frames.Count ? frames[index]
        : throw new EndOfStreamException("CAN trace ended before the transaction completed.");

    private sealed class TraceData
    {
        public required string Format { get; init; }
        public required string Source { get; init; }
        public required bool IsSimulation { get; init; }
        public required List<FrameData> Frames { get; init; }
    }

    private sealed class FrameData
    {
        public required string Direction { get; init; }
        public required string Id { get; init; }
        public required string Data { get; init; }
    }
}
