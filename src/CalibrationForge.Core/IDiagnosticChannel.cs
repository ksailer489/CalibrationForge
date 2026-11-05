namespace CalibrationForge.Core;

public interface IDiagnosticChannel
{
    uint RequestId { get; }
    uint ResponseId { get; }
    bool RequiresReconnect { get; }
    Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken = default);
    void Invalidate();
}
