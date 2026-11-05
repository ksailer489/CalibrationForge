using CalibrationForge.Core;
using System.Text;
using System.Text.Json;
using System.Globalization;

Console.WriteLine("OFFLINE ONLY - no adapter driver, ECM connection, or flashing");
var workflows = new OfflineWorkflowService();

return args switch
{
    [] or ["help"] or ["--help"] or ["-h"] => HandleHelp(),
    ["e92-protocol"] or ["adapter-requirements"] => HandleResearch(args[0]),
    ["checksum-check", _] or ["checksum-check", _, _] => await HandleChecksumCheckAsync(args, workflows),
    ["read-verify", _, _] => await HandleReadVerifyAsync(args, workflows),
    ["kernel-read-demo"] or ["kernel-read-demo", _] or ["kernel-read-replay", _, _, _, _, _] =>
        await HandleKernelReplayAsync(args),
    ["kernel-read-example", _] => await HandleKernelExampleAsync(args[1]),
    ["identity-demo"] or ["identity-replay", _] => await HandleIdentityReplayAsync(args, workflows),
    ["identity-example", _] => await HandleIdentityExampleAsync(args[1]),
    ["inspect", _] => await HandleInspectAsync(args[1]),
    ["demo"] => await HandleDemoAsync(),
    _ => HandleInvalidCommand()
};

static int HandleHelp()
{
    PrintHelp();
    return 0;
}

static int HandleResearch(string command)
{
    if (command == "e92-protocol")
    {
        foreach (var fact in E92ReadResearch.Evidence)
        {
            Console.WriteLine($"{fact.Topic}: {fact.Finding}");
            Console.WriteLine($"  Source-documented, NOT CalibrationForge hardware-verified: {fact.Source}");
        }
    }
    else
    {
        foreach (var use in Enum.GetValues<AdapterUse>())
        {
            Console.WriteLine($"{use}:");
            foreach (var requirement in AdapterRequirements.For(use)) { Console.WriteLine($"  {requirement.Description}"); }
        }
        Console.WriteLine("No physical adapter driver or validated E92 model is available. J2534/ELM/USB branding alone is insufficient.");
    }
    return 0;
}

static async Task<int> HandleChecksumCheckAsync(string[] args, IOfflineWorkflowService workflows)
{
    try
    {
        var report = await workflows.AnalyzeChecksumsAsync(args[1], args.Length == 3 ? args[2] : null);
        Console.WriteLine(report.Profile);
        foreach (var result in report.Regions)
        {
            Console.WriteLine($"{result.Name}: stored={result.Stored:X4}, expected={result.Expected:X4}, residual={result.Residual:X4}, changed bytes={result.ChangedBytes}");
        }
        Console.WriteLine($"Changed bytes outside these checks: {report.ChangesOutsideCoverage}");
        Console.WriteLine(report.Evidence);
        Console.WriteLine("Observed additive checks only. OS, secondary checksums/CVN, and vehicle validation are unresolved. No bytes repaired; NOT flash-ready.");
        if (report.Regions.Any(result => !result.MatchesObservedRule) || report.ChangesOutsideCoverage != 0)
        {
            Console.Error.WriteLine("Check failed: an observed checksum differs or modified bytes lie outside established coverage.");
            return 1;
        }
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
    {
        Console.Error.WriteLine($"Checksum analysis failed: {ex.Message} No checksum validity established.");
        return 1;
    }
}

static async Task<int> HandleReadVerifyAsync(string[] args, IOfflineWorkflowService workflows)
{
    try
    {
        var dump = await workflows.VerifyFileAsync(args[1], args[2]);
        PrintDump(dump);
        Console.WriteLine($"Bundle saved: {args[2]}. Two independent FILE reads, not an ECM dump or flash validation.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
    {
        Console.Error.WriteLine($"Read/verification/export failed: {ex.Message} No completed bundle is confirmed.");
        return 1;
    }
}

static async Task<int> HandleKernelReplayAsync(string[] args)
{
    try
    {
        var demo = args[0] == "kernel-read-demo";
        var trace = demo ? CanTraceReplay.Parse(E92ReadKernelExample.Json) : await CanTraceReplay.LoadAsync(args[1]);
        var plan = demo ? E92ReadResearch.FlashPlan(E92ReadKernelExample.Address, E92ReadKernelExample.Length, E92ReadKernelExample.ChunkSize)
            : E92ReadResearch.FlashPlan(ParseHex(args[2]), checked((int)ParseHex(args[3])),
                int.Parse(args[4], NumberStyles.None, CultureInfo.InvariantCulture));
        Console.WriteLine(trace.IsSimulation ? "SYNTHETIC SOURCE-CONTRACT FIXTURE - NOT captured E92 traffic."
            : "RECORDED TRAFFIC - provenance and active kernel version are not independently authenticated.");
        var source = new MemoryReadSource(trace.Source, trace.IsSimulation ? MemorySourceKind.Simulated
            : MemorySourceKind.RecordedCanTraffic, Guid.NewGuid());
        var reader = await E92ReadKernelReader.ProbeAsync(new IsoTpSession(trace), source);
        var dump = await new MemoryReadCoordinator(reader).ReadVerifiedAsync(plan);
        trace.AssertComplete();
        PrintDump(dump);
        var output = demo ? (args.Length == 2 ? args[1] : null) : args[5];
        if (output is not null)
        {
            await MemoryDumpFile.SaveBundleAsync(output, dump);
            Console.WriteLine($"Labeled bundle saved: {output}");
        }
        Console.WriteLine("SCPB wire contract exercised; security access, kernel upload/version authentication, stock-OS recovery, and actual E92 reads remain unimplemented/unvalidated.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException
        or ArgumentException or InvalidOperationException or TimeoutException or OverflowException)
    {
        Console.Error.WriteLine($"Kernel read replay failed: {ex.Message} No complete verified export is confirmed.");
        return 1;
    }
}

static async Task<int> HandleKernelExampleAsync(string path)
{
    try
    {
        await AtomicImageFile.SaveAsync(path, Encoding.UTF8.GetBytes(E92ReadKernelExample.Json));
        Console.WriteLine($"Synthetic source-contract fixture saved: {path}. Range: 100 hex, length: 8 hex, chunk: 4 decimal.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine($"Fixture export failed: {ex.Message} Existing files are not overwritten.");
        return 1;
    }
}

static async Task<int> HandleIdentityReplayAsync(string[] args, IOfflineWorkflowService workflows)
{
    try
    {
        var replay = await workflows.ReadIdentityAsync(args[0] == "identity-demo" ? null : args[1]);
        Console.WriteLine(replay.IsSimulation ? "SIMULATED CAN TRAFFIC - not a vehicle scan."
            : "RECORDED CAN TRAFFIC - file provenance is not independently verified.");
        Console.WriteLine($"Source: {replay.Source}");
        var identity = replay.Identity;
        Console.WriteLine($"CAN request / response IDs: 0x{identity.RequestId:X3} / 0x{identity.ResponseId:X3}");
        Console.WriteLine($"Reported VIN: {identity.Vin ?? "Not supported by the recorded PID bitmap"}");
        Console.WriteLine($"Reported calibration IDs: {(identity.CalibrationIds is null ? "Not supported" : string.Join(", ", identity.CalibrationIds))}");
        Console.WriteLine($"Reported CVNs: {(identity.CalibrationVerificationNumbers is null ? "Not supported" : string.Join(", ", identity.CalibrationVerificationNumbers.Select(value => $"{value:X8}")))}");
        Console.WriteLine("Ordered requests, responses and flow-control frames matched the complete trace.");
        Console.WriteLine("Replay does not validate adapter timing, E92 compatibility, VIN authenticity, or calibration checksums.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException
        or ArgumentException or InvalidOperationException or TimeoutException)
    {
        Console.Error.WriteLine($"Identification replay failed: {ex.Message} Controller identity is not confirmed.");
        return 1;
    }
}

static async Task<int> HandleIdentityExampleAsync(string path)
{
    try
    {
        await AtomicImageFile.SaveAsync(path, Encoding.UTF8.GetBytes(ObdIdentityExample.Json));
        Console.WriteLine($"Synthetic CAN trace saved to {path}. Use 'identity-replay <path>' to exercise the communication path.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
    {
        Console.Error.WriteLine($"Example export failed: {ex.Message} Existing files are not overwritten.");
        return 1;
    }
}

static async Task<int> HandleInspectAsync(string path)
{
    try
    {
        var image = new BinImageEditor(await File.ReadAllBytesAsync(path));
        Console.WriteLine($"Size: {image.Size} bytes");
        Console.WriteLine($"Original SHA256: {image.OriginalSha256}");
        Console.WriteLine($"Profile: {image.Profile?.Name ?? "Unknown / read-only"}");
        if (image.Profile is { } profile)
        {
            var table = profile.Table;
            var values = Enumerable.Range(0, table.Rows).SelectMany(row =>
                Enumerable.Range(0, table.Columns).Select(column => image.Read(row, column))).ToArray();
            Console.WriteLine("Exact original hash, size, header and segment metadata matched; NOT GM-verified.");
            Console.WriteLine($"{table.Name}: {table.Rows}x{table.Columns}, offset 0x{table.Offset:X}, BE u16 / 128");
            Console.WriteLine(FormattableString.Invariant($"Range: {values.Min()}..{values.Max()} experimental ms; [12,0]={image.Read(12, 0)}"));
            Console.WriteLine(ExperimentalBinProfile.Attribution);
            Console.WriteLine("Vehicle provenance unknown; not a verified 2017 Camaro calibration. Row/column indices only.");
        }

        Console.WriteLine("Checksums NOT validated or updated. Edited exports reopen read-only. NOT flash-ready.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
        Console.Error.WriteLine($"Inspection failed: {ex.Message}");
        return 1;
    }
}

static int HandleInvalidCommand()
{
    Console.Error.WriteLine("Invalid command. Use 'help' for available offline commands.");
    return 2;
}

static async Task<int> HandleDemoAsync()
{
    ICalibrationDevice device = new SimulatedCalibrationDevice();
    var original = await device.ReadImageAsync();
    var parameter = SyntheticCalibration.DemoLevel;
    var before = ByteCalibrationEditor.Read(original, parameter);
    var edited = (byte[])original.Clone();
    ByteCalibrationEditor.Write(edited, parameter, 42);
    await device.WriteImageAsync(edited);
    var verified = await device.ReadImageAsync();
    if (!verified.AsSpan().SequenceEqual(edited))
    {
        Console.Error.WriteLine("Simulator verification failed: readback differs from the edited image.");
        return 1;
    }

    Console.WriteLine($"Synthetic {parameter.Name}: {before} -> {ByteCalibrationEditor.Read(verified, parameter)}");
    Console.WriteLine("Simulator read/edit/write/readback verified. This does not establish physical safety.");
    return 0;
}

static void PrintHelp()
{
    Console.WriteLine("Usage: CalibrationForge.Cli [inspect <path>|demo|identity-demo|identity-replay <path>|identity-example <path>|help]");
    Console.WriteLine("inspect: size, SHA256 and exact public E92 sample experimental dwell summary; other BINs read-only.");
    Console.WriteLine("demo: read, edit, write and verify a synthetic 16-byte image in memory.");
    Console.WriteLine("No physical ECM access/flashing. Checksums are not updated; offline exports are not flash-ready.");
    Console.WriteLine("identity-demo: exercise standard Mode 09 identification through the CAN/ISO-TP transaction layer.");
    Console.WriteLine("identity-example: save the synthetic, ordered tx/rx CAN trace format to a new JSON file.");
    Console.WriteLine("identity-replay: verify outgoing requests/flow control against a trace and decode VIN, calibration IDs and CVNs.");
    Console.WriteLine("The transaction layer handles segmentation, flow control, separation time, cancellation and timeouts.");
    Console.WriteLine("Only standard 11-bit classic CAN normal addressing is supported; live adapter and stock E92 reading are not implemented.");
    Console.WriteLine("e92-protocol / adapter-requirements: pinned protocol evidence, unresolved steps, and adapter feature contracts.");
    Console.WriteLine("checksum-check <exact-reference.bin> [candidate.bin]: analyze observed calibration BE16 sums; never repair or claim full checksum validation.");
    Console.WriteLine("read-verify <source.bin> <new-output.zip>: two independent file reads with integrity/provenance bundle.");
    Console.WriteLine("kernel-read-demo [new-output.zip]: exercise the documented active-RAM-reader contract using a synthetic eight-byte fixture.");
    Console.WriteLine("kernel-read-example <new-trace.json>: export that source-contract fixture; it is not a vehicle capture.");
    Console.WriteLine("kernel-read-replay <trace.json> <start-hex> <length-hex> <chunk-decimal> <new-output.zip>: two-pass memory-read replay.");
    Console.WriteLine("No live driver, stock E92 memory reader, security access, kernel upload, recovery, or flashing.");
}

static uint ParseHex(string value) => uint.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

static void PrintDump(VerifiedMemoryDump dump)
{
    Console.WriteLine($"Source: {dump.Source.Kind} - {dump.Source.Name}");
    Console.WriteLine($"Range: 0x{dump.Plan.Region.Address:X8}, {dump.Plan.Region.Length} bytes; two read passes matched {dump.Plan.ReadableBytes} readable bytes.");
    Console.WriteLine($"Complete requested range: {dump.IsComplete}; unknown bytes: {dump.Plan.Region.Length - dump.Plan.ReadableBytes}");
    Console.WriteLine($"Image SHA256 (includes any labeled FF placeholders): {dump.Sha256}");
    Console.WriteLine("Matching reads establish consistency, not source authenticity, kernel version, full-ECM coverage, or flash readiness.");
}
