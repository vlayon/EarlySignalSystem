namespace EarlySignalSystem.Services;

public interface IStockPriceService
{
    Task<decimal?> GetClosingPriceAsync(string? ticker, DateTime date, CancellationToken cancellationToken = default);
    Task<decimal?> GetLatestPriceAsync(string? ticker, CancellationToken cancellationToken = default);
}
