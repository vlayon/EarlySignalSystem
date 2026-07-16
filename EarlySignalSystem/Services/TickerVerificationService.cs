using System.Globalization;
using System.Text.Json;
using EarlySignalSystem.Data;
using EarlySignalSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace EarlySignalSystem.Services;

public class TickerVerificationService : ITickerVerificationService
{
    private const string SymbolSearchApiUrl = "https://www.alphavantage.co/query?function=SYMBOL_SEARCH&keywords={0}&apikey={1}";
    private const decimal MinMatchScore = 0.8m;
    private const int MaxCompaniesPerRun = 15;
    private const int RateLimitDelayMs = 1500;
    private const string JobName = "Ticker-Verifier";

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
                    var match = await SearchSymbolAsync(company.CompanyName, apiKey, cancellationToken);
                    if (match is not null && match.MatchScore >= MinMatchScore)
                    {
                        company.Ticker = match.Symbol;
                        company.Exchange = match.Region;
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

    private async Task<SymbolMatch?> SearchSymbolAsync(string companyName, string apiKey, CancellationToken cancellationToken)
    {
        var url = string.Format(SymbolSearchApiUrl, Uri.EscapeDataString(companyName), apiKey);
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!payload.RootElement.TryGetProperty("bestMatches", out var matches) || matches.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Alpha Vantage SYMBOL_SEARCH response for {CompanyName} had no bestMatches — possibly rate limited", companyName);
            return null;
        }

        SymbolMatch? best = null;
        foreach (var match in matches.EnumerateArray())
        {
            var symbol = match.TryGetProperty("1. symbol", out var symbolElement) ? symbolElement.GetString() : null;
            // SYMBOL_SEARCH няма отделно поле за борса — "4. region" (напр. "United States", "Frankfurt")
            // е най-близкото приближение до "Exchange", което ендпойнтът предоставя.
            var region = match.TryGetProperty("4. region", out var regionElement) ? regionElement.GetString() : null;
            var matchScoreText = match.TryGetProperty("9. matchScore", out var scoreElement) ? scoreElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(symbol) ||
                !decimal.TryParse(matchScoreText, NumberStyles.Any, CultureInfo.InvariantCulture, out var matchScore))
            {
                continue;
            }

            if (best is null || matchScore > best.MatchScore)
            {
                best = new SymbolMatch(symbol, region, matchScore);
            }
        }

        return best;
    }

    private sealed record SymbolMatch(string Symbol, string? Region, decimal MatchScore);
}
