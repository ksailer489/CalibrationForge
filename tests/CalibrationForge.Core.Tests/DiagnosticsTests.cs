using System.Text.Json;
using CalibrationForge.Core;
using CalibrationForge.Desktop;

namespace CalibrationForge.Core.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"calibrationforge-diagnostics-{Guid.NewGuid():N}");

    public DiagnosticsTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public void CanCodeCountAndAllFourSystemLettersAreDecoded()
    {
        var codes = ObdDiagnostics.DecodeCodes(Convert.FromHexString("430401015234ABCDFF01"), DiagnosticCodeStatus.Stored);

        Assert.Equal(new[] { "P0101", "C1234", "B2BCD", "U3F01" }, codes.Select(code => code.Code));
        Assert.All(codes, code => Assert.Equal(DiagnosticCodeStatus.Stored, code.Status));
        Assert.Contains("airflow", codes[0].Description);
        Assert.Contains("unavailable", codes[1].Description);
    }

    [Theory]
    [InlineData("47010171", DiagnosticCodeStatus.Pending)]
    [InlineData("4A010171", DiagnosticCodeStatus.Permanent)]
    public void QueryStatusIsPreserved(string hex, DiagnosticCodeStatus status)
    {
        var code = Assert.Single(ObdDiagnostics.DecodeCodes(Convert.FromHexString(hex), status));
        Assert.Equal("P0171", code.Code);
        Assert.Equal(status, code.Status);
    }

    [Fact]
    public void AnExplicitZeroCountIsDifferentFromNoResponse()
    {
        Assert.Empty(ObdDiagnostics.DecodeCodes(Convert.FromHexString("430000000000"), DiagnosticCodeStatus.Stored));
        Assert.Throws<FormatException>(() => ObdDiagnostics.DecodeCodes(Array.Empty<byte>(), DiagnosticCodeStatus.Stored));
    }

    [Theory]
    [InlineData("43")]
    [InlineData("43020101")]
    [InlineData("4301010101")]
    [InlineData("43010000")]
    [InlineData("7F0311")]
    [InlineData("47010101")]
    public void TruncatedNegativeMismatchedOrInconsistentRepliesFail(string hex)
    {
        Assert.Throws<FormatException>(() => ObdDiagnostics.DecodeCodes(Convert.FromHexString(hex), DiagnosticCodeStatus.Stored));
    }

    [Fact]
    public void SparkReadinessDistinguishesIncompleteAndUnsupported()
    {
        var monitor = ObdDiagnostics.DecodeMonitor(Convert.FromHexString("410182076501"));

        Assert.True(monitor.LampOn);
        Assert.Equal(2, monitor.StoredCodeCount);
        Assert.False(monitor.CompressionIgnition);
        Assert.Equal(ReadinessState.Incomplete, monitor.Monitors.Single(item => item.Name == "Catalyst").State);
        Assert.Equal(ReadinessState.Complete, monitor.Monitors.Single(item => item.Name == "Oxygen sensor").State);
        Assert.Equal(ReadinessState.NotSupported, monitor.Monitors.Single(item => item.Name == "Heated catalyst").State);
    }

    [Fact]
    public void CompressionReadinessUsesItsOwnBitAssignments()
    {
        var monitor = ObdDiagnostics.DecodeMonitor(Convert.FromHexString("4101001FA280"));

        Assert.True(monitor.CompressionIgnition);
        Assert.Equal(ReadinessState.Incomplete, monitor.Monitors.Single(item => item.Name == "Misfire").State);
        Assert.Equal(ReadinessState.Complete, monitor.Monitors.Single(item => item.Name == "NOx aftertreatment").State);
        Assert.Equal(ReadinessState.Incomplete, monitor.Monitors.Single(item => item.Name == "EGR / variable valve timing").State);
        Assert.DoesNotContain(monitor.Monitors, item => item.Name == "Oxygen sensor");
    }

    [Theory]
    [InlineData("")]
    [InlineData("410100")]
    [InlineData("410200000000")]
    [InlineData("41010000000000")]
    public void MissingOrMalformedMonitorDataIsNotHealthy(string hex)
    {
        Assert.Throws<FormatException>(() => ObdDiagnostics.DecodeMonitor(Convert.FromHexString(hex)));
    }

    [Fact]
    public void CaptureReassemblesMultiFrameCodesBeforeDecoding()
    {
        var report = DiagnosticCapture.Parse(Capture(Module("Engine", "7E8",
            Response("03", "10 0A 43 04 01 01 03 00", "21 01 71 04 20"))));

        Assert.Equal(new[] { "P0101", "P0300", "P0171", "P0420" }, report.Modules[0].Codes.Select(code => code.Code));
        Assert.Equal("Not recorded", report.Modules[0].LampStatus);
        Assert.Contains("pending codes", report.Modules[0].Coverage);
    }

    [Fact]
    public void MissingConsecutiveFrameRejectsCapture()
    {
        Assert.Throws<IsoTpFrameException>(() => DiagnosticCapture.Parse(Capture(Module("Engine", "7E8",
            Response("03", "10 0A 43 04 01 01 03 00")))));
    }

    [Fact]
    public void ModuleResultsRemainSeparateAndStoredCodesDoNotInferLampOn()
    {
        var report = DiagnosticCapture.Parse(Capture(
            Module("Engine", "7E8", Response("0101", "06 41 01 01 07 00 00"), Response("03", "04 43 01 03 00")),
            Module("Transmission", "7E9", Response("07", "04 47 01 07 00"))));

        Assert.Equal("OFF", report.Modules[0].LampStatus);
        Assert.Equal("P0300", Assert.Single(report.Modules[0].Codes).Code);
        Assert.Equal("P0700", Assert.Single(report.Modules[1].Codes).Code);
        Assert.Equal("Not recorded", report.Modules[1].LampStatus);
    }

    [Fact]
    public void EmptyResponseAndMissingQueryStayUnavailable()
    {
        var module = Assert.Single(DiagnosticCapture.Parse(Capture(Module("Engine", "7E8", Response("03")))).Modules);

        Assert.Null(module.Monitor);
        Assert.Empty(module.Codes);
        Assert.Equal(4, module.MissingResponses.Count);
        Assert.Equal("Not recorded", module.LampStatus);
    }

    [Fact]
    public void DuplicateControllersAndQueriesRejectAmbiguousCaptures()
    {
        Assert.Throws<FormatException>(() => DiagnosticCapture.Parse(Capture(Module("A", "7E8"), Module("B", "7E8"))));
        Assert.Throws<FormatException>(() => DiagnosticCapture.Parse(Capture(Module("A", "7E8", Response("03"), Response("03")))));
    }

    [Fact]
    public void NonReadOnlyQueriesAndUnsupportedAddressingAreRejected()
    {
        Assert.Throws<FormatException>(() => DiagnosticCapture.Parse(Capture(Module("Engine", "7E8", Response("04", "01 44")))));
        Assert.Throws<FormatException>(() => DiagnosticCapture.Parse(Capture(Module("Engine", "18DAF110"))));
        Assert.Throws<FormatException>(() => DiagnosticCapture.Parse(Capture(Module("Engine", "7DF"))));
    }

    [Fact]
    public void InvalidVersionMissingProvenanceAndUnknownFieldsAreRejected()
    {
        Assert.Throws<FormatException>(() => DiagnosticCapture.Parse(DiagnosticCapture.ExampleJson.Replace("ObdCapture.v1", "ObdCapture.v2")));
        Assert.Throws<JsonException>(() => DiagnosticCapture.Parse(DiagnosticCapture.ExampleJson.Replace("\"isSimulation\": true,", "")));
        Assert.Throws<JsonException>(() => DiagnosticCapture.Parse(DiagnosticCapture.ExampleJson.Replace("\"modules\":", "\"unknown\": 1, \"modules\":")));
        Assert.Throws<FormatException>(() => DiagnosticCapture.Parse("null"));
    }

    [Fact]
    public void DemoUsesTheSameDecoderAndHasExplicitProvenance()
    {
        var model = new DiagnosticsViewModel();
        Assert.False(model.CanExport);
        Assert.Equal("Not recorded", model.LampStatus);

        model.LoadDemo();

        Assert.True(model.CanExport);
        Assert.Contains("SIMULATED DATA", model.Source);
        Assert.Equal("ON", model.LampStatus);
        Assert.Equal(new[] { "P0101", "P0300", "P0171" }, model.Codes.Select(code => code.Code));
        Assert.Equal(DiagnosticCodeStatus.Pending, model.Codes[2].Status);
        Assert.Contains("All four", model.Coverage);
    }

    [Fact]
    public async Task ImportAndExportPersistCapturedResultsWithoutChangingSource()
    {
        var path = Path.Combine(directory, "capture.json");
        var original = Capture(Module("Engine", "7E8", Response("07", "04 47 01 01 71")));
        await File.WriteAllTextAsync(path, original);
        var model = new DiagnosticsViewModel();

        await model.ImportAsync(path);
        var output = Path.Combine(directory, "report.txt");
        await model.ExportAsync(output);

        var saved = await File.ReadAllTextAsync(output);
        Assert.Contains("CAPTURED RESPONSES - not a live connection", saved);
        Assert.Contains("Check-engine light: Not recorded", saved);
        Assert.Contains("P0171 [Pending]: System too lean", saved);
        Assert.Contains("stored codes", saved);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InvalidImportClearsStaleReportAndAllowsRecovery()
    {
        var model = new DiagnosticsViewModel();
        model.LoadDemo();
        var invalid = Path.Combine(directory, "invalid.json");
        await File.WriteAllTextAsync(invalid, "{}");

        await Assert.ThrowsAsync<JsonException>(() => model.ImportAsync(invalid));

        Assert.True(model.CanLoad);
        Assert.False(model.CanExport);
        Assert.Empty(model.Modules);
        Assert.Equal("Not recorded", model.LampStatus);
        model.LoadDemo();
        Assert.True(model.CanExport);
    }

    [Fact]
    public async Task CanceledImportDoesNotRetainOldHealthyOrFaultData()
    {
        var path = Path.Combine(directory, "capture.json");
        await File.WriteAllTextAsync(path, DiagnosticCapture.ExampleJson);
        var model = new DiagnosticsViewModel();
        model.LoadDemo();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.ImportAsync(path, cancellation.Token));

        Assert.False(model.CanExport);
        Assert.Empty(model.Codes);
        Assert.Equal("Not recorded", model.LampStatus);
        Assert.True(model.CanLoad);
    }

    [Fact]
    public async Task ReportExportRefusesOverwriteAndRetainsLoadedReport()
    {
        var path = Path.Combine(directory, "existing.txt");
        await File.WriteAllTextAsync(path, "existing content");
        var model = new DiagnosticsViewModel();
        model.LoadDemo();

        await Assert.ThrowsAsync<IOException>(() => model.ExportAsync(path));

        Assert.Equal("existing content", await File.ReadAllTextAsync(path));
        Assert.True(model.CanExport);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task CanceledExportLeavesNoOutputOrTemporaryFile()
    {
        var model = new DiagnosticsViewModel();
        model.LoadDemo();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            model.ExportAsync(Path.Combine(directory, "canceled.txt"), cancellation.Token));

        Assert.Empty(Directory.GetFiles(directory));
        Assert.True(model.CanExport);
    }

    [Fact]
    public void EmptyCaptureNeverReportsNoVehicleFaults()
    {
        var text = DiagnosticReportFile.Format(DiagnosticCapture.Parse(Capture()));
        Assert.Contains("Vehicle condition is unknown", text);
        Assert.DoesNotContain("No codes reported", text);
    }

    [Fact]
    public void SimulationReportRemainsLabeledOutsideTheApplication()
    {
        var text = DiagnosticReportFile.Format(DiagnosticCapture.CreateDemo());
        Assert.Contains("SIMULATED DATA - not a vehicle scan", text);
        Assert.Contains("No vehicle commands were sent", text);
    }

    [Fact]
    public async Task OversizedCaptureIsRejectedBeforeParsing()
    {
        var path = Path.Combine(directory, "large.json");
        await File.WriteAllBytesAsync(path, new byte[1024 * 1024 + 1]);
        await Assert.ThrowsAsync<FormatException>(() => DiagnosticCapture.LoadAsync(path));
    }

    private static string Capture(params object[] modules) => JsonSerializer.Serialize(new
    {
        format = "CalibrationForge.ObdCapture.v1",
        source = "Recorded test responses",
        capturedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        isSimulation = false,
        modules
    });

    private static object Module(string name, string responseId, params object[] responses) => new { name, responseId, responses };
    private static object Response(string request, params string[] frames) => new { request, frames };
}
