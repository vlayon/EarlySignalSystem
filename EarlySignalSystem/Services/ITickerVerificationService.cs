namespace EarlySignalSystem.Services;

public interface ITickerVerificationService
{
    Task<int> VerifyPendingTickersAsync(CancellationToken cancellationToken = default);
}
