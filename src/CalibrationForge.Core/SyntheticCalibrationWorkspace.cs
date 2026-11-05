namespace CalibrationForge.Core;

public sealed class SyntheticCalibrationWorkspace(ICalibrationDevice device)
{
    private readonly ICalibrationDevice device = device ?? throw new ArgumentNullException(nameof(device));
    private readonly object gate = new();
    private byte[]? image;
    private byte[]? baseline;
    private bool busy;

    public bool IsLoaded { get { lock (gate) { return image is not null; } } }
    public bool IsBusy { get { lock (gate) { return busy; } } }
    public bool IsDirty { get { lock (gate) { return image is not null && !image.AsSpan().SequenceEqual(baseline); } } }
    public byte DemoLevel { get { lock (gate) { return ByteCalibrationEditor.Read(RequireImage(), SyntheticCalibration.DemoLevel); } } }
    public byte BaselineDemoLevel { get { lock (gate) { return ByteCalibrationEditor.Read(baseline ?? throw new InvalidOperationException("Read or load a synthetic image first."), SyntheticCalibration.DemoLevel); } } }

    public void LoadImage(ReadOnlySpan<byte> candidate)
    {
        lock (gate)
        {
            EnsureIdle();
            ReplaceImage(SyntheticCalibration.CopyValidatedImage(candidate));
        }
    }

    public void EditDemoLevel(int value)
    {
        lock (gate)
        {
            EnsureIdle();
            ByteCalibrationEditor.Write(RequireImage(), SyntheticCalibration.DemoLevel, value);
        }
    }

    public byte[] ExportImage()
    {
        lock (gate)
        {
            return (byte[])RequireImage().Clone();
        }
    }

    public void MarkSaved(ReadOnlySpan<byte> savedImage)
    {
        lock (gate)
        {
            EnsureIdle();
            if (!RequireImage().AsSpan().SequenceEqual(savedImage))
            {
                throw new InvalidOperationException("The image changed during save. Current edits remain unsaved.");
            }

            baseline = savedImage.ToArray();
        }
    }

    public async Task ReadSimulatorAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            EnsureIdle();
            busy = true;
        }

        try
        {
            var candidate = SyntheticCalibration.CopyValidatedImage(await device.ReadImageAsync(cancellationToken).ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                ReplaceImage(candidate);
            }
        }
        finally
        {
            lock (gate) { busy = false; }
        }
    }

    public async Task WriteAndVerifyAsync(CancellationToken cancellationToken = default)
    {
        byte[] candidate;
        lock (gate)
        {
            EnsureIdle();
            candidate = (byte[])RequireImage().Clone();
            busy = true;
        }

        try
        {
            await device.WriteImageAsync(candidate, cancellationToken).ConfigureAwait(false);
            var readback = await device.ReadImageAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!candidate.AsSpan().SequenceEqual(readback))
            {
                throw new InvalidOperationException("Simulator verification failed: readback bytes differ. Edits are retained; read the simulator again before retrying.");
            }

            lock (gate)
            {
                baseline = (byte[])candidate.Clone();
            }
        }
        finally
        {
            lock (gate) { busy = false; }
        }
    }

    private void ReplaceImage(byte[] candidate)
    {
        image = candidate;
        baseline = (byte[])candidate.Clone();
    }

    private byte[] RequireImage() => image ?? throw new InvalidOperationException("Read or load a synthetic image first.");

    private void EnsureIdle()
    {
        if (busy)
        {
            throw new InvalidOperationException("Wait for the current simulator operation to finish.");
        }
    }
}
