using EarlySignalSystem.Data;
using EarlySignalSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace EarlySignalSystem.Services;

public class CumulativeScoringService : ICumulativeScoringService
{
    private const int LookbackDays = 14;
    private const int VelocityWindowDays = 7;
    private const decimal SignalCountWeight = 5m;
    private const decimal SignalDiversityWeight = 10m;
    private const decimal HighVelocityBonus = 15m;
    private const decimal MediumVelocityBonus = 5m;
    private const int HighVelocityThreshold = 3;
    private const int MediumVelocityThreshold = 1;
    private const int MinSignalCountForInclusion = 2;
    private const int TopShortlistSize = 5;
    private const string JobName = "Cumulative-Scorer";

    private readonly AppDbContext _dbContext;
    private readonly ILogger<CumulativeScoringService> _logger;

    public CumulativeScoringService(AppDbContext dbContext, ILogger<CumulativeScoringService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<int> CalculateScoresAsync(CancellationToken cancellationToken = default)
    {
        var runLog = new RunLog
        {
            StartedAt = DateTime.UtcNow,
            Status = "Running",
            JobName = JobName
        };
        _dbContext.RunLogs.Add(runLog);
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var count = await CalculateScoresCoreAsync(cancellationToken);

            runLog.Status = "Completed";
            runLog.SignalsCollected = count;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate cumulative scores");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task<int> CalculateScoresCoreAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var windowStart = now.AddDays(-LookbackDays);
        var velocityStart = now.AddDays(-VelocityWindowDays);

        var picks = await _dbContext.CompanyPicks
            .Where(p => p.PickedAt >= windowStart)
            .ToListAsync(cancellationToken);

        var scores = new List<CumulativeScore>();

        // Пазим последния (по PickedAt) CompanyPick на всяка компания, за да вземем Sentiment/Rationale
        // за ShortlistSnapshot по-долу — CumulativeScore не съхранява тези полета.
        var latestPickByCompany = new Dictionary<CumulativeScore, CompanyPick>();

        foreach (var group in picks.GroupBy(p => new { p.CompanyName, p.Ticker }))
        {
            var companyPicks = group.ToList();
            var signalCount = companyPicks.Count;

            // Компания с 1 сигнал е noise, не достатъчно потвърден сигнал — изключваме я от shortlist-а.
            if (signalCount < MinSignalCountForInclusion)
            {
                continue;
            }

            var baseScore = companyPicks.Average(p => p.ConfidenceScore);
            var velocity = companyPicks.Count(p => p.PickedAt >= velocityStart);

            var runLogIds = companyPicks
                .Where(p => p.RunLogId.HasValue)
                .Select(p => p.RunLogId!.Value)
                .Distinct()
                .ToList();

            // Signal Diversity се измерва през RunLog: сигналите нямат директна връзка към конкретна компания,
            // но всеки CompanyPick знае от кой analysis run е произлязъл.
            var signalDiversity = runLogIds.Count == 0
                ? 0
                : await _dbContext.Signals
                    .Where(s => s.RunLogId != null && runLogIds.Contains(s.RunLogId.Value))
                    .Select(s => s.SignalType)
                    .Distinct()
                    .CountAsync(cancellationToken);

            var velocityBonus = velocity > HighVelocityThreshold
                ? HighVelocityBonus
                : velocity > MediumVelocityThreshold
                    ? MediumVelocityBonus
                    : 0m;

            var velocityLevel = velocity > HighVelocityThreshold
                ? VelocityLevel.High
                : velocity > MediumVelocityThreshold
                    ? VelocityLevel.Medium
                    : VelocityLevel.Low;

            var rawScore = baseScore + (signalCount * SignalCountWeight) + (signalDiversity * SignalDiversityWeight) + velocityBonus;

            var latestPick = companyPicks
                .OrderByDescending(p => p.PickedAt)
                .First();

            var score = new CumulativeScore
            {
                Ticker = group.Key.Ticker,
                CompanyName = group.Key.CompanyName,
                Sector = latestPick.Sector,
                Score = Math.Clamp(rawScore, 0m, 100m),
                SignalCount = signalCount,
                SignalDiversity = signalDiversity,
                VelocityLevel = velocityLevel,
                LastCalculatedAt = now
            };

            scores.Add(score);
            latestPickByCompany[score] = latestPick;
        }

        // CumulativeScores е "текущо състояние" на shortlist-а, не исторически лог — всяко изчисление презаписва изцяло.
        _dbContext.CumulativeScores.RemoveRange(_dbContext.CumulativeScores);
        _dbContext.CumulativeScores.AddRange(scores);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Calculated cumulative scores for {Count} companies", scores.Count);

        await SaveShortlistSnapshotAsync(scores, latestPickByCompany, now, cancellationToken);

        return scores.Count;
    }

    private async Task SaveShortlistSnapshotAsync(
        List<CumulativeScore> scores,
        Dictionary<CumulativeScore, CompanyPick> latestPickByCompany,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var top5 = scores
            .OrderByDescending(s => s.Score)
            .Take(TopShortlistSize)
            .ToList();

        if (top5.Count == 0)
        {
            return;
        }

        var scanDate = now.Date;

        var previousScanNumber = await _dbContext.ShortlistSnapshots
            .Where(s => s.ScanDate == scanDate)
            .Select(s => (int?)s.ScanNumber)
            .MaxAsync(cancellationToken) ?? 0;
        var scanNumber = previousScanNumber + 1;

        var rank = 1;
        foreach (var score in top5)
        {
            var latestPick = latestPickByCompany[score];

            _dbContext.ShortlistSnapshots.Add(new ShortlistSnapshot
            {
                ScanDate = scanDate,
                ScanNumber = scanNumber,
                CompanyRank = rank,
                Ticker = score.Ticker,
                CompanyName = score.CompanyName,
                Sector = score.Sector,
                CumulativeScore = score.Score,
                Sentiment = latestPick.Sentiment,
                SignalCount = score.SignalCount,
                SignalDiversity = score.SignalDiversity,
                VelocityLevel = score.VelocityLevel,
                Rationale = latestPick.Rationale,
                CreatedAt = now
            });

            rank++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
