namespace CalibrationForge.Core;

public sealed class BinWorkspace
{
    private readonly ExperimentalBinProfile profile;

    public BinWorkspace() : this(ExperimentalBinProfile.E92Sample) { }
    internal BinWorkspace(ExperimentalBinProfile profile) { this.profile = profile; }
    public BinImageEditor? Image { get; private set; }
    public string? SourcePath { get; private set; }
    public bool IsDirty => Image?.IsDirty == true;
    public sealed record PreparedLoad(string SourcePath, BinImageEditor Image);

    public void Load(string path, ReadOnlySpan<byte> bytes, bool discardUnsaved = false)
    {
        if (IsDirty && !discardUnsaved) { throw new InvalidOperationException("Discard or export unsaved BIN edits before loading another file."); }
        var fullPath = Path.GetFullPath(path);
        var candidate = new BinImageEditor(bytes, profile);
        CommitLoad(new PreparedLoad(fullPath, candidate), discardUnsaved);
    }

    public async Task<PreparedLoad> PrepareLoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        return await Task.Run(async () =>
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            BinImageEditor.ValidateSize(stream.Length);
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var extra = new byte[1];
            if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new IOException("BIN length changed during import.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = new BinImageEditor(bytes, profile);
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedLoad(fullPath, candidate);
        }, cancellationToken).ConfigureAwait(false);
    }

    public void CommitLoad(PreparedLoad prepared, bool discardUnsaved = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDirty && !discardUnsaved) { throw new InvalidOperationException("Discard or export unsaved BIN edits before loading another file."); }
        var fullPath = Path.GetFullPath(prepared.SourcePath);
        ArgumentNullException.ThrowIfNull(prepared.Image);
        SourcePath = fullPath;
        Image = prepared.Image;
    }

    public byte[] PrepareExport(string path)
    {
        if (Image?.Profile is null || !Image.IsDirty) { throw new InvalidOperationException("Apply edits to a recognized sample before exporting."); }
        if (Path.GetFullPath(path).Equals(SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Never overwrite the loaded original. Choose a new export filename.");
        }

        return Image.ExportImage();
    }
}
