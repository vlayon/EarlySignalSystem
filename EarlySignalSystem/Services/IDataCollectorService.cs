namespace EarlySignalSystem.Services;

public interface IDataCollectorService
{
    Task<int> CollectEurLexSignalsAsync(CancellationToken cancellationToken = default);
}
