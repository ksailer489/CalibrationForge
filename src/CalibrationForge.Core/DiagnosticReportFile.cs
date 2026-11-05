using System.Globalization;
using System.Text;

namespace CalibrationForge.Core;

public static class DiagnosticReportFile
{
    public static string Format(DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.AppendLine("CalibrationForge - offline diagnostic report");
        text.AppendLine(report.IsSimulation ? "SIMULATED DATA - not a vehicle scan."
            : "CAPTURED RESPONSES - not a live connection; capture metadata is supplied by the file.");
        text.AppendLine($"Source: {report.Source}");
        text.AppendLine($"Capture time: {report.CapturedAt.ToString("O", CultureInfo.InvariantCulture)}");
        text.AppendLine();
        if (report.Modules.Count == 0) { text.AppendLine("No controller responses recorded. Vehicle condition is unknown."); }

        foreach (var module in report.Modules)
        {
            text.AppendLine(module.Label);
            text.AppendLine($"Check-engine light: {module.LampStatus}");
            text.AppendLine(module.Coverage);
            if (module.Monitor is { } monitor)
            {
                text.AppendLine($"PID 01 stored-code count: {monitor.StoredCodeCount}");
                text.AppendLine($"Ignition type: {(monitor.CompressionIgnition ? "Compression" : "Spark")}");
                foreach (var item in monitor.Monitors) { text.AppendLine($"  {item.Name}: {item.Status}"); }
            }
            foreach (var code in module.Codes) { text.AppendLine($"  {code.Code} [{code.Status}]: {code.Description}"); }
            if (module.Codes.Count == 0)
            {
                text.AppendLine(module.MissingResponses.Count == 0
                    ? "No codes reported by the recorded queries."
                    : "No decoded codes. Capture is incomplete; this does not establish that the vehicle has no faults.");
            }
            text.AppendLine();
        }
        text.AppendLine("Read-only offline analysis. No vehicle commands were sent and no codes or calibration were changed.");
        return text.ToString();
    }

    public static Task SaveAsync(string path, DiagnosticReport report, CancellationToken cancellationToken = default) =>
        AtomicImageFile.SaveAsync(path, Encoding.UTF8.GetBytes(Format(report)), cancellationToken: cancellationToken);
}
