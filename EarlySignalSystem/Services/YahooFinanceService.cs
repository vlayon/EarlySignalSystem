using System.Text.Json;

namespace EarlySignalSystem.Services;

// Неофициален (undocumented), но свободен Yahoo Finance chart endpoint — без API ключ, без документиран
// dневен лимит, за разлика от Alpha Vantage (25 заявки/ден, споделени между ticker search + RSI/MACD + цени).
// Използва се като ПЪРВИЧЕН източник за дневни цени/индикатори; StockPriceService и OverboughtOversoldService
// падат обратно към Alpha Vantage само ако Yahoo няма данни за конкретния тикър (напр. непознат suffix).
// Тъй като е undocumented API, може да спре да работи или да въведе rate limiting без предупреждение —
// затова всяка грешка тук просто връща null (тих fallback), никога не хвърля.
public class YahooFinanceService : IYahooFinanceService
{
    private const string ChartApiUrl = "https://query1.finance.yahoo.com/v8/finance/chart/{0}?interval=1d&range=6mo";
    // Без User-Agent Yahoo връща 429 веднага (потвърдено на живо, 2026-08-05) — не е официално
    // документирано изискване, но е задължително на практика.
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

    // Alpha Vantage SYMBOL_SEARCH връща тикъри с AV-специфични borsa suffix-и (виж TickerVerificationService
    // ExchangeRankings) — Yahoo използва различна нотация за същите борси. Root символът съвпада между двата
    // доставчика (потвърдено на живо за DE/FRA/LSE примери), само suffix-ът се различава.
    private static readonly Dictionary<string, string> AvToYahooSuffixMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".FRK"] = ".F",
        [".DEX"] = ".DE",
        [".LON"] = ".L",
        [".PAR"] = ".PA",
        [".AMS"] = ".AS",
        [".BRU"] = ".BR",
        [".MIL"] = ".MI",
        [".MAD"] = ".MC",
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<YahooFinanceService> _logger;

    // Без пауза между заявки Yahoo започва да връща 429 след 2-3 бързи последователни извиквания
    // (потвърдено на живо, 2026-08-05, когато Cumulative Scorer удари 4 тикъра назад-назад) — същата
    // защита като при Alpha Vantage-кия delay в StockPriceService/OverboughtOversoldService.
    private bool _hasMadeRequest;

    // Close и Volume идват от ЕДИН и същ chart отговор — кешираме суровия parse по тикър в рамките на
    // instance-а, за да не удряме Yahoo два пъти, когато LateDetectionService поиска и двете за същия тикър.
    private readonly Dictionary<string, ChartData?> _chartCache = new(StringComparer.OrdinalIgnoreCase);

    public YahooFinanceService(HttpClient httpClient, ILogger<YahooFinanceService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<DateTime, decimal>?> GetDailyClosesAsync(string ticker, CancellationToken cancellationToken = default)
    {
        var chart = await GetChartDataAsync(ticker, cancellationToken);
        return chart?.Closes;
    }

    public async Task<IReadOnlyDictionary<DateTime, long>?> GetDailyVolumesAsync(string ticker, CancellationToken cancellationToken = default)
    {
        var chart = await GetChartDataAsync(ticker, cancellationToken);
        return chart?.Volumes;
    }

    private async Task<ChartData?> GetChartDataAsync(string ticker, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ticker))
        {
            return null;
        }

        if (_chartCache.TryGetValue(ticker, out var cached))
        {
            return cached;
        }

        var yahooSymbol = ConvertAvTickerToYahoo(ticker);

        try
        {
            if (_hasMadeRequest)
            {
                await Task.Delay(800, cancellationToken);
            }
            _hasMadeRequest = true;

            using var request = new HttpRequestMessage(HttpMethod.Get, string.Format(ChartApiUrl, Uri.EscapeDataString(yahooSymbol)));
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Yahoo Finance returned {StatusCode} for {Ticker} ({YahooSymbol})", response.StatusCode, ticker, yahooSymbol);
                _chartCache[ticker] = null;
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var root = payload.RootElement.GetProperty("chart");
            var results = root.GetProperty("result");
            if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            {
                _chartCache[ticker] = null;
                return null;
            }

            var result = results[0];
            var timestamps = result.GetProperty("timestamp");
            var quote = result.GetProperty("indicators").GetProperty("quote")[0];
            var closes = quote.GetProperty("close");
            var hasVolume = quote.TryGetProperty("volume", out var volumes);

            var closeSeries = new Dictionary<DateTime, decimal>();
            var volumeSeries = new Dictionary<DateTime, long>();
            for (var i = 0; i < timestamps.GetArrayLength(); i++)
            {
                var date = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime.Date;

                if (closes[i].ValueKind == JsonValueKind.Number)
                {
                    closeSeries[date] = closes[i].GetDecimal();
                }

                if (hasVolume && volumes[i].ValueKind == JsonValueKind.Number)
                {
                    volumeSeries[date] = volumes[i].GetInt64();
                }
            }

            var chart = closeSeries.Count > 0 ? new ChartData(closeSeries, volumeSeries) : null;
            _chartCache[ticker] = chart;
            return chart;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Yahoo Finance chart data for {Ticker} ({YahooSymbol})", ticker, yahooSymbol);
            _chartCache[ticker] = null;
            return null;
        }
    }

    private static string ConvertAvTickerToYahoo(string avTicker)
    {
        foreach (var (avSuffix, yahooSuffix) in AvToYahooSuffixMap)
        {
            if (avTicker.EndsWith(avSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return avTicker[..^avSuffix.Length] + yahooSuffix;
            }
        }

        // Без AV suffix (US тикъри и OTC ADR-и) — Yahoo ползва същия bare symbol.
        return avTicker;
    }

    private sealed record ChartData(IReadOnlyDictionary<DateTime, decimal> Closes, IReadOnlyDictionary<DateTime, long> Volumes);
}
