using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalibrationForge.Core;

public static class MemoryDumpFile
{
    public static async Task SaveBundleAsync(string path, VerifiedMemoryDump dump, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dump);
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Dump bundles require a .zip extension.", nameof(path));
        }
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            var imageName = dump.IsComplete ? "image.bin" : "image.partial.bin";
            using (var image = archive.CreateEntry(imageName).Open())
            {
                await image.WriteAsync(dump.CopyImage(), cancellationToken).ConfigureAwait(false);
            }
            using var manifest = archive.CreateEntry("manifest.json").Open();
            await JsonSerializer.SerializeAsync(manifest, new
            {
                format = "CalibrationForge.VerifiedDump.v1",
                source = dump.Source,
                dump.CompletedAt,
                imageFile = imageName,
                startAddress = $"0x{dump.Plan.Region.Address:X8}",
                length = dump.Plan.Region.Length,
                readableBytes = dump.Plan.ReadableBytes,
                completeRequestedRange = dump.IsComplete,
                independentReadPasses = 2,
                sha256 = dump.Sha256,
                excludedRanges = dump.Plan.Exclusions.Select(gap => new
                {
                    address = $"0x{gap.Address:X8}",
                    gap.Length,
                    fileOffset = gap.Address - dump.Plan.Region.Address,
                    placeholder = "FF - unknown bytes, not data read from the controller"
                }),
                warning = "Repeated-read consistency only. Source claims are not independently authenticated. Not checksum-validated or flash-ready."
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            },
                cancellationToken).ConfigureAwait(false);
        }
        await AtomicImageFile.SaveAsync(path, buffer.ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
