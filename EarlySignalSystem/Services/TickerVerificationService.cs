using System.Globalization;
using System.Text.Json;
using EarlySignalSystem.Data;
using EarlySignalSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace EarlySignalSystem.Services;

public class TickerVerificationService : ITickerVerificationService
{
    private const string SymbolSearchApiUrl = "https://www.alphavantage.co/query?function=SYMBOL_SEARCH&keywords={0}&apikey={1}";
    private const decimal MinMatchScore = 0.7m;
    private const int MaxCompaniesPerRun = 15;
    private const int RateLimitDelayMs = 1500;
    private const string JobName = "Ticker-Verifier";

    // Alpha Vantage SYMBOL_SEARCH "4. region" връща борсов град/държава (напр. "Frankfurt", "United Kingdom"),
    // не самата борса — нормализираме към държавно име (за Companies.Exchange) и ranked preference
    // (по-нисък Rank = по-предпочитана борса). Регион, който не съвпада с нищо тук, е извън обхвата
    // (Индия, Бразилия, OTC и т.н.) и се отхвърля изцяло.
    private static readonly (string RegionKeyword, int Rank, string ExchangeLabel)[] ExchangeRankings =
    [
        ("United States", 1, "United States"),
        ("Frankfurt", 2, "Germany"),
        ("XETRA", 2, "Germany"),
        ("United Kingdom", 3, "United Kingdom"),
        ("Paris", 4, "France"),
        ("Amsterdam", 4, "Netherlands"),
        ("Brussels", 4, "Belgium"),
        ("Milan", 5, "Italy"),
        ("Madrid", 6, "Spain"),
        ("Switzerland", 7, "Switzerland"),
        ("Zurich", 7, "Switzerland"),
        ("Sweden", 7, "Sweden"),
        ("Stockholm", 7, "Sweden"),
        ("Denmark", 7, "Denmark"),
        ("Copenhagen", 7, "Denmark"),
        ("Finland", 7, "Finland"),
        ("Helsinki", 7, "Finland"),
        ("Norway", 7, "Norway"),
        ("Oslo", 7, "Norway"),
    ];

    private readonly AppDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TickerVerificationService> _logger;

    public TickerVerificationService(AppDbContext dbContext, HttpClient httpClient, IConfiguration configuration, ILogger<TickerVerificationService> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<int> VerifyPendingTickersAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["AlphaVantage:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AlphaVantage:ApiKey configuration is missing.");
        }

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
            // Ограничаваме на MaxCompaniesPerRun (15), за да не изчерпаме Alpha Vantage free tier
            // (25 заявки/ден) — компаниите, останали TickerVerified = false, се обработват в следващия run.
            var pendingCompanies = await _dbContext.Companies
                .Where(c => !c.TickerVerified)
                .OrderBy(c => c.CreatedAt)
                .Take(MaxCompaniesPerRun)
                .ToListAsync(cancellationToken);

            var verified = 0;
            for (var i = 0; i < pendingCompanies.Count; i++)
            {
                var company = pendingCompanies[i];

                if (i > 0)
                {
                    await Task.Delay(RateLimitDelayMs, cancellationToken);
                }

                try
                {
                    var candidates = await SearchSymbolAsync(company.CompanyName, apiKey, cancellationToken);

                    // Сортираме по (1) exchange rank, (2) matchScore DESC — предпочитаме резултат от по-желана
                    // борса дори при по-нисък matchScore, вместо просто най-добрия overall match.
                    var best = candidates
                        .OrderBy(c => c.ExchangeRank)
                        .ThenByDescending(c => c.MatchScore)
                        .FirstOrDefault();

                    if (best is not null)
                    {
                        company.Ticker = best.Symbol;
                        company.Exchange = best.ExchangeLabel;
                        company.TickerVerified = true;
                        company.TickerVerifiedAt = DateTime.UtcNow;
                        verified++;
                    }
                }
                catch (Exception ex)
                {
                    // Един неуспешен symbol lookup (rate limit, мрежов проблем) не бива да проваля целия run —
                    // компанията си остава TickerVerified = false и ще се пробва пак следващия път.
                    _logger.LogWarning(ex, "Failed to verify ticker for {CompanyName}", company.CompanyName);
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            runLog.Status = "Completed";
            runLog.SignalsCollected = verified;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return verified;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify pending tickers");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task<List<RankedMatch>> SearchSymbolAsync(string companyName, string apiKey, CancellationToken cancellationToken)
    {
        var url = string.Format(SymbolSearchApiUrl, Uri.EscapeDataString(companyName), apiKey);
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!payload.RootElement.TryGetProperty("bestMatches", out var matches) || matches.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Alpha Vantage SYMBOL_SEARCH response for {CompanyName} had no bestMatches — possibly rate limited", companyName);
            return [];
        }

        var candidates = new List<RankedMatch>();
        foreach (var match in matches.EnumerateArray())
        {
            var symbol = match.TryGetProperty("1. symbol", out var symbolElement) ? symbolElement.GetString() : null;
            var region = match.TryGetProperty("4. region", out var regionElement) ? regionElement.GetString() : null;
            var matchScoreText = match.TryGetProperty("9. matchScore", out var scoreElement) ? scoreElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(symbol) ||
                !decimal.TryParse(matchScoreText, NumberStyles.Any, CultureInfo.InvariantCulture, out var matchScore) ||
                matchScore < MinMatchScore)
            {
                continue;
            }

            var exchange = ClassifyExchange(region);
            if (exchange is null)
            {
                continue;
            }

            candidates.Add(new RankedMatch(symbol, exchange.Value.Label, exchange.Value.Rank, matchScore));
        }

        return candidates;
    }

    private static (int Rank, string Label)? ClassifyExchange(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return null;
        }

        foreach (var (keyword, rank, label) in ExchangeRankings)
        {
            if (region.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return (rank, label);
            }
        }

        return null;
    }

    private sealed record RankedMatch(string Symbol, string ExchangeLabel, int ExchangeRank, decimal MatchScore);
}
