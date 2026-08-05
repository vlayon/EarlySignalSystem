namespace EarlySignalSystem.Services;

public interface IYahooFinanceService
{
    Task<IReadOnlyDictionary<DateTime, decimal>?> GetDailyClosesAsync(string ticker, CancellationToken cancellationToken = default);
}
