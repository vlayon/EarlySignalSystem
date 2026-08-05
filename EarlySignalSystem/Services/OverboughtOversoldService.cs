using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EarlySignalSystem.Data;
using EarlySignalSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace EarlySignalSystem.Services;

public class OverboughtOversoldService : IOverboughtOversoldService
{
    private const string RsiApiUrl = "https://www.alphavantage.co/query?function=RSI&symbol={0}&interval=daily&time_period=14&series_type=close&apikey={1}";
    private const string MacdApiUrl = "https://www.alphavantage.co/query?function=MACD&symbol={0}&interval=daily&series_type=close&apikey={1}";
    private const string ClaudeApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ClaudeModel = "claude-haiku-4-5";
    private const int ClaudeMaxTokens = 512;
    private const int TopShortlistSize = 5;
    private const string JobName = "Technical-Assessor";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IYahooFinanceService _yahooFinanceService;
    private readonly ILogger<OverboughtOversoldService> _logger;

    // RSI/MACD кеш по тикер за текущия run — AssessTopCompaniesAsync и повторни AssessAsync извиквания
    // за същия тикер споделят един fetch, за да пестим Alpha Vantage free tier (25 заявки/ден).
    private readonly Dictionary<string, decimal?> _rsiCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (decimal? Macd, decimal? Signal, decimal? Histogram)> _macdCache = new(StringComparer.OrdinalIgnoreCase);
    // Yahoo дневните closes се ползват и от RSI, и от MACD изчислението — един fetch на тикър, споделен
    // между двете, вместо RSI и MACD да викат Yahoo поотделно.
    private readonly Dictionary<string, IReadOnlyDictionary<DateTime, decimal>?> _yahooClosesCache = new(StringComparer.OrdinalIgnoreCase);

    private const int RsiPeriod = 14;
    private const int MacdFastPeriod = 12;
    private const int MacdSlowPeriod = 26;
    private const int MacdSignalPeriod = 9;

    public OverboughtOversoldService(AppDbContext dbContext, HttpClient httpClient, IConfiguration configuration, IYahooFinanceService yahooFinanceService, ILogger<OverboughtOversoldService> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _configuration = configuration;
        _yahooFinanceService = yahooFinanceService;
        _logger = logger;
    }

    public async Task<int> AssessTopCompaniesAsync(CancellationToken cancellationToken = default)
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
            var topCompanies = await _dbContext.CumulativeScores
                .OrderByDescending(s => s.Score)
                .Take(TopShortlistSize)
                .ToListAsync(cancellationToken);

            var assessed = 0;
            foreach (var company in topCompanies)
            {
                if (string.IsNullOrWhiteSpace(company.Ticker))
                {
                    continue;
                }

                try
                {
                    var assessment = await AssessAsync(company.Ticker, null, cancellationToken);
                    if (assessment is not null)
                    {
                        assessed++;
                    }
                }
                catch (Exception ex)
                {
                    // Една неуспешна оценка (rate limit, невалиден symbol) не бива да проваля целия run.
                    _logger.LogWarning(ex, "Failed to assess {Ticker}", company.Ticker);
                }
            }

            runLog.Status = "Completed";
            runLog.SignalsCollected = assessed;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return assessed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to assess top companies");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    public async Task<TechnicalAssessment?> AssessAsync(string ticker, decimal? rsi = null, CancellationToken cancellationToken = default)
    {
        var alphaVantageApiKey = _configuration["AlphaVantage:ApiKey"];
        if (string.IsNullOrWhiteSpace(alphaVantageApiKey))
        {
            throw new InvalidOperationException("AlphaVantage:ApiKey configuration is missing.");
        }

        var claudeApiKey = _configuration["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(claudeApiKey))
        {
            throw new InvalidOperationException("Anthropic:ApiKey configuration is missing.");
        }

        var resolvedRsi = rsi ?? await GetRsiAsync(ticker, alphaVantageApiKey, cancellationToken);
        var macd = await GetMacdAsync(ticker, alphaVantageApiKey, cancellationToken);

        var company = await _dbContext.CumulativeScores
            .Where(s => s.Ticker == ticker)
            .OrderByDescending(s => s.LastCalculatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var sentiment = await _dbContext.CompanyPicks
            .Where(p => p.Ticker == ticker)
            .OrderByDescending(p => p.PickedAt)
            .Select(p => p.Sentiment)
            .FirstOrDefaultAsync(cancellationToken);

        var synthesis = await SynthesizeAssessmentAsync(
            ticker,
            resolvedRsi,
            macd,
            company?.LatestPrice,
            company?.PriceChangePercent,
            sentiment,
            company?.SignalCount ?? 0,
            claudeApiKey,
            cancellationToken);

        if (synthesis is null)
        {
            return null;
        }

        var assessment = new TechnicalAssessment
        {
            Ticker = ticker,
            Assessment = NormalizeAssessment(synthesis.Assessment),
            Reason = synthesis.Reason,
            RSI = resolvedRsi,
            MACDSignal = macd.Signal,
            AssessedAt = DateTime.UtcNow
        };

        _dbContext.TechnicalAssessments.Add(assessment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return assessment;
    }

    private async Task<decimal?> GetRsiAsync(string ticker, string apiKey, CancellationToken cancellationToken)
    {
        if (_rsiCache.TryGetValue(ticker, out var cached))
        {
            return cached;
        }

        var yahooCloses = await GetYahooClosesAsync(ticker, cancellationToken);
        if (yahooCloses is not null)
        {
            var closesAscending = yahooCloses.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToList();
            var yahooRsi = CalculateRsi(closesAscending);
            if (yahooRsi is not null)
            {
                _rsiCache[ticker] = yahooRsi;
                return yahooRsi;
            }
        }

        try
        {
            var url = string.Format(RsiApiUrl, Uri.EscapeDataString(ticker), apiKey);
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!payload.RootElement.TryGetProperty("Technical Analysis: RSI", out var series))
            {
                _logger.LogWarning("Alpha Vantage response for {Ticker} had no RSI series — possibly rate limited or an invalid symbol", ticker);
                _rsiCache[ticker] = null;
                return null;
            }

            var latest = series.EnumerateObject()
                .OrderByDescending(p => p.Name, StringComparer.Ordinal)
                .FirstOrDefault();

            var rsi = latest.Value.ValueKind == JsonValueKind.Object &&
                latest.Value.TryGetProperty("RSI", out var rsiElement) &&
                decimal.TryParse(rsiElement.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedRsi)
                ? parsedRsi
                : (decimal?)null;

            _rsiCache[ticker] = rsi;
            return rsi;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch RSI for {Ticker}", ticker);
            _rsiCache[ticker] = null;
            return null;
        }
    }

    private async Task<(decimal? Macd, decimal? Signal, decimal? Histogram)> GetMacdAsync(string ticker, string apiKey, CancellationToken cancellationToken)
    {
        if (_macdCache.TryGetValue(ticker, out var cached))
        {
            return cached;
        }

        var yahooCloses = await GetYahooClosesAsync(ticker, cancellationToken);
        if (yahooCloses is not null)
        {
            var closesAscending = yahooCloses.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToList();
            var yahooMacd = CalculateMacd(closesAscending);
            if (yahooMacd.Macd is not null)
            {
                _macdCache[ticker] = yahooMacd;
                return yahooMacd;
            }
        }

        try
        {
            var url = string.Format(MacdApiUrl, Uri.EscapeDataString(ticker), apiKey);
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!payload.RootElement.TryGetProperty("Technical Analysis: MACD", out var series))
            {
                _logger.LogWarning("Alpha Vantage response for {Ticker} had no MACD series — possibly rate limited or an invalid symbol", ticker);
                _macdCache[ticker] = (null, null, null);
                return (null, null, null);
            }

            var latest = series.EnumerateObject()
                .OrderByDescending(p => p.Name, StringComparer.Ordinal)
                .FirstOrDefault();

            decimal? ReadValue(string propertyName) =>
                latest.Value.ValueKind == JsonValueKind.Object &&
                latest.Value.TryGetProperty(propertyName, out var element) &&
                decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : (decimal?)null;

            var result = (ReadValue("MACD"), ReadValue("MACD_Signal"), ReadValue("MACD_Hist"));
            _macdCache[ticker] = result;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch MACD for {Ticker}", ticker);
            _macdCache[ticker] = (null, null, null);
            return (null, null, null);
        }
    }

    private async Task<ClaudeAssessmentResult?> SynthesizeAssessmentAsync(
        string ticker,
        decimal? rsi,
        (decimal? Macd, decimal? Signal, decimal? Histogram) macd,
        decimal? currentPrice,
        decimal? priceChangePercent,
        string? sentiment,
        int signalCount,
        string claudeApiKey,
        CancellationToken cancellationToken)
    {
        var macdText = macd.Macd.HasValue
            ? $"{macd.Macd:0.00} (signal {macd.Signal:0.00}, histogram {macd.Histogram:0.00})"
            : "n/a";

        var prompt =
            $"Given these technical indicators and fundamental signals for {ticker}, provide a brief 2-sentence assessment: " +
            "Is this stock overbought (likely to correct), oversold (potential entry point), or neutral? " +
            $"RSI: {(rsi.HasValue ? rsi.Value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a")}, " +
            $"MACD: {macdText}, " +
            $"Current price: {(currentPrice.HasValue ? $"${currentPrice.Value.ToString("0.00", CultureInfo.InvariantCulture)}" : "n/a")}, " +
            $"Price change since first signal: {(priceChangePercent.HasValue ? priceChangePercent.Value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a")}%, " +
            $"Fundamental sentiment: {sentiment ?? "Neutral"}, Signal count: {signalCount}. " +
            "Return JSON: {\"assessment\": \"Overbought\"|\"Oversold\"|\"Neutral\", \"reason\": \"brief explanation\"}";

        var requestBody = new
        {
            model = ClaudeModel,
            max_tokens = ClaudeMaxTokens,
            messages = new[] { new { role = "user", content = prompt } }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ClaudeApiUrl)
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("x-api-key", claudeApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadFromJsonAsync<ClaudeMessageResponse>(cancellationToken: cancellationToken);
        var text = responseBody?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Claude API returned no text content for {Ticker} technical assessment", ticker);
            return null;
        }

        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            _logger.LogWarning("Could not locate a JSON object in the Claude response for {Ticker}: {Text}", ticker, text);
            return null;
        }

        var json = text[jsonStart..(jsonEnd + 1)];
        try
        {
            return JsonSerializer.Deserialize<ClaudeAssessmentResult>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Claude technical assessment JSON for {Ticker}: {Json}", ticker, json);
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<DateTime, decimal>?> GetYahooClosesAsync(string ticker, CancellationToken cancellationToken)
    {
        if (_yahooClosesCache.TryGetValue(ticker, out var cached))
        {
            return cached;
        }

        var series = await _yahooFinanceService.GetDailyClosesAsync(ticker, cancellationToken);
        _yahooClosesCache[ticker] = series;
        return series;
    }

    // Wilder's smoothing — същият подход, който Alpha Vantage документира за собствения си RSI endpoint,
    // затова стойностите би трябвало да съвпадат в разумни граници с AV-версията за приемственост.
    private static decimal? CalculateRsi(List<decimal> closesAscending)
    {
        if (closesAscending.Count <= RsiPeriod)
        {
            return null;
        }

        decimal avgGain = 0, avgLoss = 0;
        for (var i = 1; i <= RsiPeriod; i++)
        {
            var change = closesAscending[i] - closesAscending[i - 1];
            if (change > 0) { avgGain += change; } else { avgLoss += -change; }
        }

        avgGain /= RsiPeriod;
        avgLoss /= RsiPeriod;

        for (var i = RsiPeriod + 1; i < closesAscending.Count; i++)
        {
            var change = closesAscending[i] - closesAscending[i - 1];
            var gain = change > 0 ? change : 0;
            var loss = change < 0 ? -change : 0;
            avgGain = (avgGain * (RsiPeriod - 1) + gain) / RsiPeriod;
            avgLoss = (avgLoss * (RsiPeriod - 1) + loss) / RsiPeriod;
        }

        if (avgLoss == 0)
        {
            return 100m;
        }

        var rs = avgGain / avgLoss;
        return 100m - 100m / (1 + rs);
    }

    // Стандартен MACD(12,26,9): MACD line = EMA12 - EMA26, Signal = EMA9 на MACD line-а, Histogram = MACD - Signal.
    private static (decimal? Macd, decimal? Signal, decimal? Histogram) CalculateMacd(List<decimal> closesAscending)
    {
        if (closesAscending.Count <= MacdSlowPeriod + MacdSignalPeriod)
        {
            return (null, null, null);
        }

        var emaFast = CalculateEmaSeries(closesAscending, MacdFastPeriod);
        var emaSlow = CalculateEmaSeries(closesAscending, MacdSlowPeriod);

        // emaFast стартира по-рано във времето от emaSlow (по-къс период) — подравняваме по реалната дата,
        // не по индекс, преди да извадим двете серии.
        var offset = emaFast.Count - emaSlow.Count;
        var macdLine = new List<decimal>(emaSlow.Count);
        for (var i = 0; i < emaSlow.Count; i++)
        {
            macdLine.Add(emaFast[i + offset] - emaSlow[i]);
        }

        var signalLine = CalculateEmaSeries(macdLine, MacdSignalPeriod);
        if (signalLine.Count == 0)
        {
            return (null, null, null);
        }

        var macd = macdLine[^1];
        var signal = signalLine[^1];
        return (macd, signal, macd - signal);
    }

    // Стандартна EMA: seed-ната със SMA на първите `period` стойности, после стандартна exponential recurrence.
    private static List<decimal> CalculateEmaSeries(List<decimal> values, int period)
    {
        var result = new List<decimal>();
        if (values.Count < period)
        {
            return result;
        }

        var multiplier = 2m / (period + 1);
        var ema = values.Take(period).Average();
        result.Add(ema);

        for (var i = period; i < values.Count; i++)
        {
            ema = (values[i] - ema) * multiplier + ema;
            result.Add(ema);
        }

        return result;
    }

    private static string NormalizeAssessment(string? assessment) => assessment?.Trim() switch
    {
        "Overbought" => "Overbought",
        "Oversold" => "Oversold",
        _ => "Neutral"
    };

    private sealed class ClaudeMessageResponse
    {
        [JsonPropertyName("content")]
        public List<ClaudeContentBlock>? Content { get; set; }
    }

    private sealed class ClaudeContentBlock
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private sealed class ClaudeAssessmentResult
    {
        public string? Assessment { get; set; }
        public string? Reason { get; set; }
    }
}
