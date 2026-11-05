namespace CalibrationForge.Core;

public interface ICalibrationDevice
{
    Task<byte[]> ReadImageAsync(CancellationToken cancellationToken = default);

    Task WriteImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default);
}
