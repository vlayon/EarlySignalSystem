namespace EarlySignalSystem.Services;

public interface IAiAnalyzerService
{
    Task<int> AnalyzeSignalsAsync(CancellationToken cancellationToken = default);
}
