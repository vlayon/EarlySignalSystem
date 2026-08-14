namespace EarlySignalSystem.Services;

public interface IYahooFinanceService
{
    Task<IReadOnlyDictionary<DateTime, decimal>?> GetDailyClosesAsync(string ticker, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<DateTime, long>?> GetDailyVolumesAsync(string ticker, CancellationToken cancellationToken = default);
}
