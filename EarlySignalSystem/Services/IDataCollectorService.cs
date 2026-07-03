namespace EarlySignalSystem.Services;

public interface IDataCollectorService
{
    Task<int> CollectEurLexSignalsAsync(CancellationToken cancellationToken = default);
    Task<int> CollectSecEdgarSignalsAsync(CancellationToken cancellationToken = default);
    Task<int> CollectTedSignalsAsync(CancellationToken cancellationToken = default);
    Task<int> CollectOecdSignalsAsync(CancellationToken cancellationToken = default);
    Task<int> CollectEsmaSignalsAsync(CancellationToken cancellationToken = default);
}
