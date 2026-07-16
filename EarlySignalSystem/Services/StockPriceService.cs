using System.Globalization;
using System.Text.Json;

namespace EarlySignalSystem.Services;

public class StockPriceService : IStockPriceService
{
    private const string DailySeriesApiUrl = "https://www.alphavantage.co/query?function=TIME_SERIES_DAILY&symbol={0}&apikey={1}";
    // Сигналите се събират и през уикенда/празници, когато борсата е затворена — гледаме до 7 дни назад
    // за най-близкия предходен търговски ден вместо да връщаме null за точна дата без данни.
    private const int MaxLookbackDays = 7;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StockPriceService> _logger;

    // Кешираме целия daily time series по тикер (не по ticker+дата) — GetClosingPriceAsync и
    // GetLatestPriceAsync за същия тикер в рамките на един run споделят един и същ Alpha Vantage fetch,
    // за да пестим free tier бюджета (25 заявки/ден).
    private readonly Dictionary<string, IReadOnlyDictionary<DateTime, decimal>?> _seriesCache = new(StringComparer.OrdinalIgnoreCase);

    // true след първата реално изпратена Alpha Vantage заявка в текущия service instance — закъснението
    // за rate limiting пада само между отделни HTTP заявки, не и преди самата първа.
    private bool _hasMadeRequest;

    public StockPriceService(HttpClient httpClient, IConfiguration configuration, ILogger<StockPriceService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<decimal?> GetClosingPriceAsync(string? ticker, DateTime date, CancellationToken cancellationToken = default)
    {
        var series = await GetSeriesAsync(ticker, cancellationToken);
        if (series is null || series.Count == 0)
        {
            return null;
        }

        for (var i = 0; i <= MaxLookbackDays; i++)
        {
            if (series.TryGetValue(date.Date.AddDays(-i), out var price))
            {
                return price;
            }
        }

        return null;
    }

    public async Task<decimal?> GetLatestPriceAsync(string? ticker, CancellationToken cancellationToken = default)
    {
        var series = await GetSeriesAsync(ticker, cancellationToken);
        if (series is null || series.Count == 0)
        {
            return null;
        }

        return series[series.Keys.Max()];
    }

    private async Task<IReadOnlyDictionary<DateTime, decimal>?> GetSeriesAsync(string? ticker, CancellationToken cancellationToken)
    {
        // Компанията още няма верифициран ticker (Companies.TickerVerified = false) — пропускаме price
        // lookup-а тихо, това е нормално/очаквано състояние, не грешка.
        if (string.IsNullOrWhiteSpace(ticker))
        {
            return null;
        }

        if (_seriesCache.TryGetValue(ticker, out var cached))
        {
            return cached;
        }

        var apiKey = _configuration["AlphaVantage:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AlphaVantage:ApiKey configuration is missing.");
        }

        try
        {
            if (_hasMadeRequest)
            {
                await Task.Delay(1500, cancellationToken);
            }
            _hasMadeRequest = true;

            var url = string.Format(DailySeriesApiUrl, Uri.EscapeDataString(ticker), apiKey);
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!payload.RootElement.TryGetProperty("Time Series (Daily)", out var seriesElement))
            {
                _logger.LogWarning("Alpha Vantage response for {Ticker} had no \"Time Series (Daily)\" — possibly rate limited or an invalid symbol", ticker);
                _seriesCache[ticker] = null;
                return null;
            }

            var series = new Dictionary<DateTime, decimal>();
            foreach (var day in seriesElement.EnumerateObject())
            {
                if (!DateTime.TryParse(day.Name, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                {
                    continue;
                }

                if (day.Value.TryGetProperty("4. close", out var closeElement) &&
                    decimal.TryParse(closeElement.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
                {
                    series[parsedDate.Date] = close;
                }
            }

            _seriesCache[ticker] = series;
            return series;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Alpha Vantage daily series for {Ticker}", ticker);
            _seriesCache[ticker] = null;
            return null;
        }
    }
}
