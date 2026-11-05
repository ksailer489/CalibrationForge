using CalibrationForge.Core;

namespace CalibrationForge.Core.Tests;

public class CalibrationTests
{
    [Fact]
    public async Task SyntheticFileSaveLoadRoundtripReplacesExistingImageAndLeavesNoTemporaryFile()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, $"calibrationforge-file-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.bin");
        try
        {
            var workspace = new SyntheticCalibrationWorkspace(new SimulatedCalibrationDevice());
            await workspace.ReadSimulatorAsync();
            await SyntheticImageFile.SaveAsync(path, workspace.ExportImage());
            workspace.EditDemoLevel(80);
            var expected = workspace.ExportImage();

            await SyntheticImageFile.SaveAsync(path, expected);
            workspace.MarkSaved(expected);
            Assert.False(workspace.IsDirty);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
            Assert.Equal(expected, await SyntheticImageFile.LoadAsync(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task CanceledSyntheticSavePreservesDestinationAndUnsavedWorkspace()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, $"calibrationforge-file-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.bin");
        try
        {
            var workspace = new SyntheticCalibrationWorkspace(new SimulatedCalibrationDevice());
            await workspace.ReadSimulatorAsync();
            var original = workspace.ExportImage();
            await SyntheticImageFile.SaveAsync(path, original);
            workspace.EditDemoLevel(60);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => SyntheticImageFile.SaveAsync(path, workspace.ExportImage(), cancellation.Token));

            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.True(workspace.IsDirty);
            Assert.Equal(60, workspace.DemoLevel);
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task InvalidSyntheticFilesAndFailedSaveLeavePreviousDestinationUnchanged()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, $"calibrationforge-file-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.bin");
        var invalidPath = Path.Combine(directory, "invalid.bin");
        try
        {
            var workspace = new SyntheticCalibrationWorkspace(new SimulatedCalibrationDevice());
            await workspace.ReadSimulatorAsync();
            var original = workspace.ExportImage();
            await SyntheticImageFile.SaveAsync(path, original);
            workspace.EditDemoLevel(42);
            var invalid = new byte[SyntheticCalibration.ImageLength];
            invalid[SyntheticCalibration.DemoLevel.Offset] = 101;
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SyntheticImageFile.SaveAsync(path, invalid));
            await Assert.ThrowsAsync<ArgumentException>(() => SyntheticImageFile.SaveAsync(path, new byte[17]));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.True(workspace.IsDirty);

            using (var lockedDestination = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var error = await Record.ExceptionAsync(() => SyntheticImageFile.SaveAsync(path, workspace.ExportImage()));
                Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a file access failure, got {error?.GetType().Name ?? "success"}.");
            }

            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
            await File.WriteAllBytesAsync(invalidPath, invalid);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SyntheticImageFile.LoadAsync(invalidPath));
            await File.WriteAllBytesAsync(invalidPath, new byte[17]);
            await Assert.ThrowsAsync<ArgumentException>(() => SyntheticImageFile.LoadAsync(invalidPath));
            Assert.Equal(42, workspace.DemoLevel);
            Assert.True(workspace.IsDirty);
        }
        finally
        {
            File.Delete(path);
            File.Delete(invalidPath);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task WorkspaceEditsExportAndSaveLoadRoundtripKeepAccurateDirtyState()
    {
        var workspace = new SyntheticCalibrationWorkspace(new SimulatedCalibrationDevice());
        Assert.False(workspace.IsLoaded);
        Assert.False(workspace.IsDirty);
        Assert.Throws<InvalidOperationException>(() => workspace.EditDemoLevel(42));
        Assert.Throws<InvalidOperationException>(() => workspace.ExportImage());
        await workspace.ReadSimulatorAsync();
        Assert.Equal(25, workspace.DemoLevel);
        Assert.False(workspace.IsDirty);

        workspace.EditDemoLevel(42);
        Assert.True(workspace.IsDirty);
        workspace.EditDemoLevel(25);
        Assert.False(workspace.IsDirty);
        workspace.EditDemoLevel(42);
        var saved = workspace.ExportImage();
        Assert.Equal(42, ByteCalibrationEditor.Read(saved, SyntheticCalibration.DemoLevel));
        workspace.MarkSaved(saved);
        Assert.False(workspace.IsDirty);

        var loaded = new SyntheticCalibrationWorkspace(new SimulatedCalibrationDevice());
        loaded.LoadImage(saved);
        Array.Clear(saved);
        Assert.Equal(42, loaded.DemoLevel);
        Assert.False(loaded.IsDirty);
        workspace.EditDemoLevel(80);
        Assert.Throws<InvalidOperationException>(() => workspace.MarkSaved(loaded.ExportImage()));
        Assert.True(workspace.IsDirty);
        await workspace.WriteAndVerifyAsync();
        Assert.Equal(80, workspace.DemoLevel);
        Assert.False(workspace.IsDirty);
        await workspace.ReadSimulatorAsync();
        Assert.Equal(80, workspace.DemoLevel);
    }

    [Fact]
    public async Task WorkspaceRejectsInvalidLoadAndEditWithoutDiscardingUnsavedChanges()
    {
        var workspace = new SyntheticCalibrationWorkspace(new SimulatedCalibrationDevice());
        await workspace.ReadSimulatorAsync();
        workspace.EditDemoLevel(60);
        var expected = workspace.ExportImage();
        Assert.Throws<ArgumentException>(() => workspace.LoadImage(new byte[17]));
        var invalid = new byte[SyntheticCalibration.ImageLength];
        invalid[SyntheticCalibration.DemoLevel.Offset] = 101;
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.LoadImage(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.EditDemoLevel(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.EditDemoLevel(101));
        Assert.Equal(expected, workspace.ExportImage());
        Assert.True(workspace.IsDirty);
    }

    [Fact]
    public async Task WorkspaceChecksEveryReadbackByteAndRetainsDirtyImageOnMismatch()
    {
        var device = new ControlledDevice();
        var workspace = new SyntheticCalibrationWorkspace(device);
        await workspace.ReadSimulatorAsync();
        workspace.EditDemoLevel(42);
        var expected = workspace.ExportImage();
        device.CorruptReadback = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.WriteAndVerifyAsync());

        Assert.Contains("verification failed", error.Message);
        Assert.Equal(expected, workspace.ExportImage());
        Assert.True(workspace.IsDirty);
        Assert.False(workspace.IsBusy);
        device.CorruptReadback = false;
        await workspace.WriteAndVerifyAsync();
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task WorkspaceBlocksEditsAndReplacementUntilPendingReadCompletes()
    {
        var device = new ControlledDevice();
        var workspace = new SyntheticCalibrationWorkspace(device);
        await workspace.ReadSimulatorAsync();
        workspace.EditDemoLevel(42);
        device.PendingRead = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var pending = workspace.ReadSimulatorAsync(cancellation.Token);
        Assert.True(workspace.IsBusy);
        Assert.Throws<InvalidOperationException>(() => workspace.EditDemoLevel(60));
        Assert.Throws<InvalidOperationException>(() => workspace.LoadImage(new byte[SyntheticCalibration.ImageLength]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.WriteAndVerifyAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(42, workspace.DemoLevel);
        Assert.True(workspace.IsDirty);
        Assert.False(workspace.IsBusy);
    }

    [Fact]
    public async Task WorkspaceBlocksEditingDuringWriteAndCanceledVerificationStaysDirty()
    {
        var device = new ControlledDevice();
        var workspace = new SyntheticCalibrationWorkspace(device);
        await workspace.ReadSimulatorAsync();
        workspace.EditDemoLevel(60);
        device.PendingRead = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var write = workspace.WriteAndVerifyAsync(cancellation.Token);
        Assert.True(workspace.IsBusy);
        Assert.Throws<InvalidOperationException>(() => workspace.EditDemoLevel(80));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(60, workspace.DemoLevel);
        Assert.True(workspace.IsDirty);
        Assert.False(workspace.IsBusy);
        device.PendingRead = null;
        await workspace.WriteAndVerifyAsync();
        Assert.False(workspace.IsDirty);
    }

    private sealed class ControlledDevice : ICalibrationDevice
    {
        private byte[] image = new byte[SyntheticCalibration.ImageLength];
        public bool CorruptReadback { get; set; }
        public TaskCompletionSource<byte[]>? PendingRead { get; set; }

        public Task<byte[]> ReadImageAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PendingRead is not null)
            {
                return PendingRead.Task.WaitAsync(cancellationToken);
            }

            var readback = (byte[])image.Clone();
            if (CorruptReadback) { readback[0] ^= 1; }
            return Task.FromResult(readback);
        }

        public Task WriteImageAsync(ReadOnlyMemory<byte> candidate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image = candidate.ToArray();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadSnapshotsAndWriteInputsDoNotAliasDeviceState()
    {
        ICalibrationDevice device = new SimulatedCalibrationDevice();
        var snapshot = await device.ReadImageAsync();
        var expected = (byte[])snapshot.Clone();
        snapshot[0] = 99;
        Assert.Equal(expected, await device.ReadImageAsync());

        ByteCalibrationEditor.Write(snapshot, SyntheticCalibration.DemoLevel, 60);
        await device.WriteImageAsync(snapshot);
        expected = (byte[])snapshot.Clone();
        Array.Clear(snapshot);
        Assert.Equal(expected, await device.ReadImageAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public async Task InvalidLengthWriteLeavesStateUnchanged(int length)
    {
        ICalibrationDevice device = new SimulatedCalibrationDevice();
        var before = await device.ReadImageAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => device.WriteImageAsync(new byte[length]));
        Assert.Equal(before, await device.ReadImageAsync());
    }

    [Fact]
    public async Task SimulatorReadEditWriteReadbackWorkflow()
    {
        ICalibrationDevice device = new SimulatedCalibrationDevice();
        var initial = await device.ReadImageAsync();
        var edited = (byte[])initial.Clone();
        ByteCalibrationEditor.Write(edited, SyntheticCalibration.DemoLevel, 42);

        Assert.Equal(initial, await device.ReadImageAsync());
        await device.WriteImageAsync(edited);
        var readback = await device.ReadImageAsync();

        Assert.Equal(edited, readback);
        Assert.Equal(42, ByteCalibrationEditor.Read(readback, SyntheticCalibration.DemoLevel));
        Assert.NotEqual(initial[SyntheticCalibration.DemoLevel.Offset], readback[SyntheticCalibration.DemoLevel.Offset]);
        for (var offset = 0; offset < readback.Length; offset++)
        {
            if (offset != SyntheticCalibration.DemoLevel.Offset)
            {
                Assert.Equal(initial[offset], readback[offset]);
            }
        }
    }

    [Fact]
    public async Task CancellationPropagatesAndCanceledWriteDoesNotModifyState()
    {
        ICalibrationDevice device = new SimulatedCalibrationDevice();
        var before = await device.ReadImageAsync();
        var edited = (byte[])before.Clone();
        ByteCalibrationEditor.Write(edited, SyntheticCalibration.DemoLevel, 80);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var readError = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.ReadImageAsync(cancellation.Token));
        var writeError = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.WriteImageAsync(edited, cancellation.Token));

        Assert.Equal(cancellation.Token, readError.CancellationToken);
        Assert.Equal(cancellation.Token, writeError.CancellationToken);
        Assert.Equal(before, await device.ReadImageAsync());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(256)]
    [InlineData(int.MaxValue)]
    public void InvalidEditRangeOrByteTypeLeavesImageUnchanged(int value)
    {
        var image = new byte[SyntheticCalibration.ImageLength];
        ByteCalibrationEditor.Write(image, SyntheticCalibration.DemoLevel, 25);
        var before = (byte[])image.Clone();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ByteCalibrationEditor.Write(image, SyntheticCalibration.DemoLevel, value));
        Assert.Equal(before, image);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void EditAcceptsInclusiveRangeBoundaries(int value)
    {
        var image = new byte[SyntheticCalibration.ImageLength];

        ByteCalibrationEditor.Write(image, SyntheticCalibration.DemoLevel, value);

        Assert.Equal(value, ByteCalibrationEditor.Read(image, SyntheticCalibration.DemoLevel));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void OutOfBoundsEditAndReadFailExplicitly(int length)
    {
        var image = new byte[length];
        var before = (byte[])image.Clone();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ByteCalibrationEditor.Read(image, SyntheticCalibration.DemoLevel));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ByteCalibrationEditor.Write(image, SyntheticCalibration.DemoLevel, 50));
        Assert.Equal(before, image);
    }

    [Fact]
    public void InvalidDefinitionsAndNullParametersAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ByteCalibrationParameter(null!, 0, 0, 1));
        Assert.Throws<ArgumentException>(() => new ByteCalibrationParameter(" ", 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ByteCalibrationParameter("Demo", -1, 0, 1));
        Assert.Throws<ArgumentException>(() => new ByteCalibrationParameter("Demo", 0, 2, 1));
        Assert.Throws<ArgumentNullException>(() => ByteCalibrationEditor.Read(new byte[1], null!));
        Assert.Throws<ArgumentNullException>(() => ByteCalibrationEditor.Write(new byte[1], null!, 0));
    }
}
