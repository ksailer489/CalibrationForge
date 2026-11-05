using System.Globalization;

namespace CalibrationForge.Core;

public enum DiagnosticCodeStatus { Stored, Pending, Permanent }
public enum ReadinessState { NotSupported, Complete, Incomplete }

public sealed record DiagnosticCode(string Code, DiagnosticCodeStatus Status)
{
    public string Description => ObdDiagnostics.Describe(Code);
}

public sealed record ReadinessMonitor(string Name, ReadinessState State)
{
    public string Status => State == ReadinessState.NotSupported ? "Not supported" : State.ToString();
}

public sealed record MonitorStatus(bool LampOn, int StoredCodeCount, bool CompressionIgnition,
    IReadOnlyList<ReadinessMonitor> Monitors);

public sealed class DiagnosticModule
{
    internal DiagnosticModule(string name, int responseId, MonitorStatus? monitor,
        List<DiagnosticCode> codes, List<string> missingResponses)
    {
        Label = $"{name} (0x{responseId:X3})";
        Monitor = monitor;
        Codes = codes.AsReadOnly();
        MissingResponses = missingResponses.AsReadOnly();
    }

    public string Label { get; }
    public MonitorStatus? Monitor { get; }
    public IReadOnlyList<DiagnosticCode> Codes { get; }
    public IReadOnlyList<string> MissingResponses { get; }
    public string LampStatus => Monitor is null ? "Not recorded" : Monitor.LampOn ? "ON" : "OFF";
    public string Coverage => MissingResponses.Count == 0 ? "All four supported queries recorded."
        : $"Not recorded: {string.Join(", ", MissingResponses)}.";
    public override string ToString() => Label;
}

public sealed class DiagnosticReport
{
    internal DiagnosticReport(string source, DateTimeOffset capturedAt, bool isSimulation,
        List<DiagnosticModule> modules)
    {
        Source = source;
        CapturedAt = capturedAt;
        IsSimulation = isSimulation;
        Modules = modules.AsReadOnly();
    }

    public string Source { get; }
    public DateTimeOffset CapturedAt { get; }
    public bool IsSimulation { get; }
    public IReadOnlyList<DiagnosticModule> Modules { get; }
}

public static class ObdDiagnostics
{
    public static IReadOnlyList<DiagnosticCode> DecodeCodes(ReadOnlySpan<byte> payload, DiagnosticCodeStatus status)
    {
        var service = status switch
        {
            DiagnosticCodeStatus.Stored => 0x43,
            DiagnosticCodeStatus.Pending => 0x47,
            DiagnosticCodeStatus.Permanent => 0x4A,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
        if (payload.Length < 2 || payload[0] != service)
        {
            throw new FormatException($"Expected CAN OBD response service 0x{service:X2} and a DTC count.");
        }

        var expectedLength = 2 + payload[1] * 2;
        if (payload.Length < expectedLength)
        {
            throw new FormatException("DTC response is shorter than its declared code count.");
        }
        for (var index = expectedLength; index < payload.Length; index++)
        {
            if (payload[index] != 0) { throw new FormatException("Unexpected data after the declared DTC entries."); }
        }

        var codes = new List<DiagnosticCode>();
        for (var index = 2; index < expectedLength; index += 2)
        {
            var encoded = (payload[index] << 8) | payload[index + 1];
            if (encoded == 0) { throw new FormatException("DTC count includes an empty code entry."); }
            var code = string.Create(CultureInfo.InvariantCulture,
                $"{SystemLetter(encoded >> 14)}{(encoded >> 12) & 3}{encoded & 0xFFF:X3}");
            codes.Add(new DiagnosticCode(code, status));
        }
        return codes.AsReadOnly();
    }

    public static MonitorStatus DecodeMonitor(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 6 || payload[0] != 0x41 || payload[1] != 0x01)
        {
            throw new FormatException("Expected a six-byte Mode 01 PID 01 monitor-status response.");
        }

        var monitors = new List<ReadinessMonitor>();
        var flags = payload[3];
        AddMonitor(monitors, "Misfire", flags, flags >> 4, 0);
        AddMonitor(monitors, "Fuel system", flags, flags >> 4, 1);
        AddMonitor(monitors, "Comprehensive components", flags, flags >> 4, 2);
        var compression = (flags & 0x08) != 0;
        if (compression)
        {
            AddMonitor(monitors, "NMHC catalyst", payload[4], payload[5], 0);
            AddMonitor(monitors, "NOx aftertreatment", payload[4], payload[5], 1);
            AddMonitor(monitors, "Boost pressure", payload[4], payload[5], 3);
            AddMonitor(monitors, "Exhaust gas sensor", payload[4], payload[5], 5);
            AddMonitor(monitors, "Particulate filter", payload[4], payload[5], 6);
            AddMonitor(monitors, "EGR / variable valve timing", payload[4], payload[5], 7);
        }
        else
        {
            AddMonitor(monitors, "Catalyst", payload[4], payload[5], 0);
            AddMonitor(monitors, "Heated catalyst", payload[4], payload[5], 1);
            AddMonitor(monitors, "Evaporative system", payload[4], payload[5], 2);
            AddMonitor(monitors, "Secondary air", payload[4], payload[5], 3);
            AddMonitor(monitors, "A/C refrigerant", payload[4], payload[5], 4);
            AddMonitor(monitors, "Oxygen sensor", payload[4], payload[5], 5);
            AddMonitor(monitors, "Oxygen sensor heater", payload[4], payload[5], 6);
            AddMonitor(monitors, "EGR / variable valve timing", payload[4], payload[5], 7);
        }
        return new MonitorStatus((payload[2] & 0x80) != 0, payload[2] & 0x7F, compression, monitors.AsReadOnly());
    }

    public static string Describe(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length == 5 && code.StartsWith("P030", StringComparison.Ordinal) && code[4] is >= '1' and <= '8')
        {
            return $"Cylinder {code[4]} misfire detected";
        }
        return code switch
        {
            "P0101" => "Mass airflow sensor range / performance",
            "P0117" => "Engine coolant temperature sensor circuit low",
            "P0118" => "Engine coolant temperature sensor circuit high",
            "P0171" => "System too lean (bank 1)",
            "P0174" => "System too lean (bank 2)",
            "P0300" => "Random / multiple-cylinder misfire detected",
            "P0420" => "Catalyst efficiency below threshold (bank 1)",
            "P0430" => "Catalyst efficiency below threshold (bank 2)",
            "P0442" => "Evaporative system small leak detected",
            "P0455" => "Evaporative system large leak detected",
            "P0456" => "Evaporative system very small leak detected",
            "P0606" => "Control module processor fault",
            _ => "Description unavailable; consult vehicle service information"
        };
    }

    private static char SystemLetter(int value) => value switch
    {
        0 => 'P', 1 => 'C', 2 => 'B', 3 => 'U',
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static void AddMonitor(List<ReadinessMonitor> monitors, string name, int supported, int incomplete, int bit)
    {
        var mask = 1 << bit;
        var state = (supported & mask) == 0 ? ReadinessState.NotSupported
            : (incomplete & mask) != 0 ? ReadinessState.Incomplete : ReadinessState.Complete;
        monitors.Add(new ReadinessMonitor(name, state));
    }
}
