using EarlySignalSystem.Data;
using EarlySignalSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace EarlySignalSystem.Services;

public class CumulativeScoringService : ICumulativeScoringService
{
    private const int LookbackDays = 14;
    private const int VelocityWindowDays = 7;
    private const decimal DiversityPointsWeight = 20m;
    private const decimal CountPointsWeight = 3m;
    private const decimal HighVelocityBonus = 10m;
    private const decimal MediumVelocityBonus = 5m;
    private const int HighVelocityThreshold = 3;
    private const int MediumVelocityThreshold = 1;
    // Raw Score = BaseScore(0-100) + DiversityPoints + CountPoints + VelocityBonus; нормализираме до 100
    // спрямо теоретичния максимум на Raw Score, за да не клампва тихо всеки силен сигнал на 100.
    private const decimal MaxPossibleRawScore = 230m;
    private const int TopShortlistSize = 5;
    private const string JobName = "Cumulative-Scorer";

    private readonly AppDbContext _dbContext;
    private readonly IStockPriceService _stockPriceService;
    private readonly ILogger<CumulativeScoringService> _logger;

    public CumulativeScoringService(AppDbContext dbContext, IStockPriceService stockPriceService, ILogger<CumulativeScoringService> logger)
    {
        _dbContext = dbContext;
        _stockPriceService = stockPriceService;
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
        var initialSaveCount = await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("SaveChangesAsync wrote {RowsSaved} row(s) after creating RunLog {RunLogId}", initialSaveCount, runLog.Id);

        try
        {
            var count = await CalculateScoresCoreAsync(cancellationToken);

            runLog.Status = "Completed";
            runLog.SignalsCollected = count;
            runLog.CompletedAt = DateTime.UtcNow;
            var completedSaveCount = await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("SaveChangesAsync wrote {RowsSaved} row(s) after marking RunLog {RunLogId} Completed", completedSaveCount, runLog.Id);

            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate cumulative scores");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            var failedSaveCount = await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("SaveChangesAsync wrote {RowsSaved} row(s) after marking RunLog {RunLogId} Failed", failedSaveCount, runLog.Id);
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

        // Пазим и всички CompanyPicks на компанията (не само последния), за да намерим FirstSignalDate
        // през CompanyPickSignals -> Signals за всяка компания по-долу.
        var allPicksByCompany = new Dictionary<CumulativeScore, List<CompanyPick>>();

        foreach (var group in picks.GroupBy(p => new { p.CompanyName, p.Ticker }))
        {
            var companyPicks = group.ToList();
            var signalCount = companyPicks.Count;

            var runLogIds = companyPicks
                .Where(p => p.RunLogId.HasValue)
                .Select(p => p.RunLogId!.Value)
                .Distinct()
                .ToList();

            // Signal Diversity се измерва през RunLog: сигналите нямат директна връзка към конкретна компания,
            // но всеки CompanyPick знае от кой analysis run е произлязъл. Изчисляваме го преди inclusion filter-а
            // по-долу, защото решението за включване вече зависи от diversity, не само от count.
            var signalDiversity = runLogIds.Count == 0
                ? 0
                : await _dbContext.Signals
                    .Where(s => s.RunLogId != null && runLogIds.Contains(s.RunLogId.Value))
                    .Select(s => s.SignalType)
                    .Distinct()
                    .CountAsync(cancellationToken);

            // Компания с diversity 1 и само 1 сигнал е noise — изключваме я. Diversity >= 2 винаги минава
            // (потвърдено от повече от един тип източник), diversity 1 изисква поне 2 picks.
            var isEligible = signalDiversity >= 2 || (signalDiversity == 1 && signalCount >= 2);
            if (!isEligible)
            {
                continue;
            }

            var baseScore = companyPicks.Average(p => p.ConfidenceScore);
            var velocity = companyPicks.Count(p => p.PickedAt >= velocityStart);

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

            var diversityPoints = signalDiversity * DiversityPointsWeight;
            var countPoints = signalCount * CountPointsWeight;
            var rawScore = baseScore + diversityPoints + countPoints + velocityBonus;
            var normalizedScore = rawScore / MaxPossibleRawScore * 100m;

            var latestPick = companyPicks
                .OrderByDescending(p => p.PickedAt)
                .First();

            var score = new CumulativeScore
            {
                Ticker = group.Key.Ticker,
                CompanyName = group.Key.CompanyName,
                Sector = latestPick.Sector,
                Score = Math.Clamp(normalizedScore, 0m, 100m),
                SignalCount = signalCount,
                SignalDiversity = signalDiversity,
                VelocityLevel = velocityLevel,
                LastCalculatedAt = now
            };

            scores.Add(score);
            latestPickByCompany[score] = latestPick;
            allPicksByCompany[score] = companyPicks;
        }

        // FirstSignalDate трябва да е известен за ВСИЧКИ компании, не само топ 5 — участва като tie-breaker
        // в ordering-а за Shortlist snapshot-а по-долу. Това е чиста DB заявка (без Alpha Vantage), евтино е.
        foreach (var score in scores)
        {
            score.FirstSignalDate = await GetFirstSignalDateAsync(allPicksByCompany[score], cancellationToken);
        }

        // Ценовото обогатяване (Alpha Vantage) е скъпо (free tier: 25 заявки/ден) — правим го само за
        // топ 5 по Score, не за всичките ~66 компании, преди да презапишем таблицата.
        var topForPriceEnrichment = scores
            .OrderByDescending(s => s.Score)
            .Take(TopShortlistSize)
            .ToList();

        foreach (var score in topForPriceEnrichment)
        {
            await EnrichWithPriceDataAsync(score, now, cancellationToken);
        }

        // CumulativeScores е "текущо състояние" на shortlist-а, не исторически лог — всяко изчисление презаписва изцяло.
        _dbContext.CumulativeScores.RemoveRange(_dbContext.CumulativeScores);
        _dbContext.CumulativeScores.AddRange(scores);
        var cumulativeScoresSaveCount = await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("SaveChangesAsync wrote {RowsSaved} row(s) while saving {Count} CumulativeScores", cumulativeScoresSaveCount, scores.Count);

        _logger.LogInformation("Calculated cumulative scores for {Count} companies", scores.Count);

        await SaveShortlistSnapshotAsync(scores, latestPickByCompany, now, cancellationToken);

        return scores.Count;
    }

    private async Task<DateTime?> GetFirstSignalDateAsync(List<CompanyPick> companyPicks, CancellationToken cancellationToken)
    {
        var pickIds = companyPicks.Select(p => p.Id).ToList();

        var signalIds = await _dbContext.CompanyPickSignals
            .Where(cps => pickIds.Contains(cps.CompanyPickId))
            .Select(cps => cps.SignalId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return signalIds.Count == 0
            ? null
            : await _dbContext.Signals
                .Where(s => signalIds.Contains(s.Id))
                .MinAsync(s => (DateTime?)s.PublishedAt, cancellationToken);
    }

    private async Task EnrichWithPriceDataAsync(CumulativeScore score, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var priceOnFirstSignalDate = score.FirstSignalDate.HasValue
                ? await _stockPriceService.GetClosingPriceAsync(score.Ticker, score.FirstSignalDate.Value, cancellationToken)
                : null;
            score.PriceOnFirstSignalDate = priceOnFirstSignalDate;

            var latestPrice = await _stockPriceService.GetLatestPriceAsync(score.Ticker, cancellationToken);
            score.LatestPrice = latestPrice;
            score.LatestPriceDate = latestPrice.HasValue ? now.Date : null;

            score.PriceChangePercent = priceOnFirstSignalDate is > 0 && latestPrice.HasValue
                ? (latestPrice.Value - priceOnFirstSignalDate.Value) / priceOnFirstSignalDate.Value * 100m
                : null;
        }
        catch (Exception ex)
        {
            // Alpha Vantage грешка (rate limit, невалиден symbol, мрежов проблем) за една компания не бива
            // да проваля целия scoring run — оставяме ценовите полета null и продължаваме.
            _logger.LogWarning(ex, "Failed to enrich price data for {Ticker}", score.Ticker);
        }
    }

    private async Task SaveShortlistSnapshotAsync(
        List<CumulativeScore> scores,
        Dictionary<CumulativeScore, CompanyPick> latestPickByCompany,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var top5 = scores
            .OrderByDescending(s => s.SignalDiversity)
            .ThenByDescending(s => s.SignalCount)
            .ThenByDescending(s => s.FirstSignalDate)
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

        var shortlistSaveCount = await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "SaveChangesAsync wrote {RowsSaved} row(s) while saving {Count} ShortlistSnapshot entries for scan {ScanDate:yyyy-MM-dd} #{ScanNumber}",
            shortlistSaveCount, top5.Count, scanDate, scanNumber);
    }
}
