using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalibrationForge.Core;

public static class DiagnosticCapture
{
    private const int MaximumCaptureBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public const string ExampleJson = """
        {
          "format": "CalibrationForge.ObdCapture.v1",
          "source": "Example responses - not a vehicle scan",
          "capturedAt": "2026-01-01T00:00:00Z",
          "isSimulation": true,
          "modules": [
            {
              "name": "Example engine controller",
              "responseId": "7E8",
              "responses": [
                { "request": "0101", "frames": ["06 41 01 82 07 65 01"] },
                { "request": "03", "frames": ["06 43 02 01 01 03 00"] },
                { "request": "07", "frames": ["04 47 01 01 71"] },
                { "request": "0A", "frames": ["02 4A 00"] }
              ]
            }
          ]
        }
        """;

    public static DiagnosticReport CreateDemo()
    {
        var capture = Deserialize(ExampleJson);
        capture.CapturedAt = DateTimeOffset.UtcNow;
        return Build(capture);
    }

    public static DiagnosticReport Parse(string json) => Build(Deserialize(json));

    public static async Task<DiagnosticReport> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length is < 2 or > MaximumCaptureBytes)
        {
            throw new FormatException("Capture files must contain JSON and be no larger than 1 MiB.");
        }
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(json);
    }

    private static CaptureData Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumCaptureBytes)
        {
            throw new FormatException("Capture files must be no larger than 1 MiB.");
        }
        return JsonSerializer.Deserialize<CaptureData>(json, Options)
            ?? throw new FormatException("Capture must be a JSON object.");
    }

    private static DiagnosticReport Build(CaptureData capture)
    {
        if (!string.Equals(capture.Format, "CalibrationForge.ObdCapture.v1", StringComparison.Ordinal))
        {
            throw new FormatException("Unsupported capture format. Expected CalibrationForge.ObdCapture.v1.");
        }
        if (string.IsNullOrWhiteSpace(capture.Source) || capture.Source.Length > 512 ||
            capture.CapturedAt == default || capture.Modules is null || capture.Modules.Count > 8)
        {
            throw new FormatException("Capture requires a source, capture time, and no more than eight modules.");
        }

        var modules = new List<DiagnosticModule>();
        var ids = new HashSet<int>();
        foreach (var module in capture.Modules)
        {
            if (module is null || string.IsNullOrWhiteSpace(module.Name) || module.Name.Length > 80 ||
                !int.TryParse(module.ResponseId, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) ||
                id is < 0x7E8 or > 0x7EF || !ids.Add(id) ||
                module.Responses is null || module.Responses.Count > 4)
            {
                throw new FormatException("Modules require a name, a unique 7E8-7EF response ID, and up to four queries.");
            }

            MonitorStatus? monitor = null;
            var codes = new List<DiagnosticCode>();
            var recorded = new HashSet<string>(StringComparer.Ordinal);
            var requested = new HashSet<string>(StringComparer.Ordinal);
            foreach (var response in module.Responses)
            {
                if (response is null || response.Request is null ||
                    response.Request is not ("0101" or "03" or "07" or "0A") ||
                    !requested.Add(response.Request) || response.Frames is null || response.Frames.Count > 128)
                {
                    throw new FormatException("Queries must be unique 0101, 03, 07, or 0A entries with CAN data frames.");
                }
                if (response.Frames.Count == 0) { continue; }

                var frames = response.Frames.Select(ParseFrame).ToArray();
                var payload = IsoTpCodec.Decode(frames);
                if (response.Request == "0101")
                {
                    monitor = ObdDiagnostics.DecodeMonitor(payload);
                }
                else
                {
                    var status = response.Request switch
                    {
                        "03" => DiagnosticCodeStatus.Stored,
                        "07" => DiagnosticCodeStatus.Pending,
                        "0A" => DiagnosticCodeStatus.Permanent,
                        _ => throw new FormatException("Unsupported diagnostic query.")
                    };
                    codes.AddRange(ObdDiagnostics.DecodeCodes(payload, status));
                }
                recorded.Add(response.Request);
            }

            var missing = new List<string>();
            if (!recorded.Contains("0101")) { missing.Add("check-engine light / readiness"); }
            if (!recorded.Contains("03")) { missing.Add("stored codes"); }
            if (!recorded.Contains("07")) { missing.Add("pending codes"); }
            if (!recorded.Contains("0A")) { missing.Add("permanent codes"); }
            modules.Add(new DiagnosticModule(module.Name, id, monitor, codes, missing));
        }
        return new DiagnosticReport(capture.Source, capture.CapturedAt, capture.IsSimulation, modules);
    }

    private static byte[] ParseFrame(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) { throw new FormatException("CAN data frames cannot be empty."); }
        var compact = string.Concat(hex.Where(character => !char.IsWhiteSpace(character)));
        if (compact.Length is < 4 or > 16)
        {
            throw new FormatException("Classic CAN data frames must contain 2 through 8 hexadecimal bytes.");
        }
        return Convert.FromHexString(compact);
    }

    private sealed class CaptureData
    {
        public required string Format { get; init; }
        public required string Source { get; init; }
        public required DateTimeOffset CapturedAt { get; set; }
        public required bool IsSimulation { get; init; }
        public required List<ModuleData> Modules { get; init; }
    }

    private sealed class ModuleData
    {
        public required string Name { get; init; }
        public required string ResponseId { get; init; }
        public required List<ResponseData> Responses { get; init; }
    }

    private sealed class ResponseData
    {
        public required string Request { get; init; }
        public required List<string> Frames { get; init; }
    }
}
