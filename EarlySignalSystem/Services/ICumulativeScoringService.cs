namespace EarlySignalSystem.Services;

public interface ICumulativeScoringService
{
    Task<int> CalculateScoresAsync(CancellationToken cancellationToken = default);
}
