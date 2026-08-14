using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EarlySignalSystem.Data;
using EarlySignalSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace EarlySignalSystem.Services;

public class TickerVerificationService : ITickerVerificationService
{
    private const string SymbolSearchApiUrl = "https://www.alphavantage.co/query?function=SYMBOL_SEARCH&keywords={0}&apikey={1}";
    private const decimal MinMatchScore = 0.7m;
    // По-лош от всеки Rank в ExchangeRankings — OTC печели сравнението само ако няма никаква истинска борса в резултатите.
    private const int LastResortOtcRank = 99;

    // OpenFIGI — много по-широко глобално покритие от Alpha Vantage (точно за целта е създаден —
    // мапване на тикъри между борси), но по-тесен free tier rate limit: 5 заявки/мин без ключ
    // (20/мин с безплатен ключ — не изискваме ключ, само го ползваме ако е конфигуриран).
    private const string OpenFigiSearchUrl = "https://api.openfigi.com/v3/search";
    private const int OpenFigiRateLimitDelayMs = 12_500;

    // Yahoo Finance-ото собствено search API — свободно, без ключ, без документиран дневен лимит,
    // последен реален опит преди AI-hint fallback-а. Undocumented (виж YahooFinanceService за същата
    // "изисква User-Agent, иначе 429" забележка, потвърдена на живо и тук).
    private const string YahooSearchApiUrl = "https://query1.finance.yahoo.com/v1/finance/search";
    private const string YahooUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
    private const int MaxCompaniesPerRun = 15;
    private const int RateLimitDelayMs = 1500;
    private const string JobName = "Ticker-Verifier";

    // SEC публикува безплатно, без ключ и без rate limit пълния списък с тикъри на всички US-listed
    // компании. Пробваме го първо — покрива голяма част от picks (NYSE/NASDAQ), без изобщо да пипа
    // Alpha Vantage free tier квотата (25 заявки/ден, споделена и с RSI/MACD/price lookups).
    private const string SecCompanyTickersUrl = "https://www.sec.gov/files/company_tickers.json";
    private const string SecUserAgent = "EarlySignalSystem research@earlysignalsystem.local";
    private static readonly TimeSpan SecTickerCacheDuration = TimeSpan.FromHours(24);
    private static readonly SemaphoreSlim SecTickerLoadLock = new(1, 1);
    private static readonly string[] CompanySuffixes =
        ["incorporated", "inc", "corporation", "corp", "co", "company", "ltd", "limited", "plc", "llc", "holdings", "holding", "group"];

    private static Dictionary<string, string>? _secTickersByNormalizedName;
    private static Dictionary<string, string>? _secOtcTickersByNormalizedName;
    private static DateTime _secTickersLoadedAt = DateTime.MinValue;

    // Alpha Vantage SYMBOL_SEARCH "4. region" връща борсов град/държава (напр. "Frankfurt", "United Kingdom"),
    // не самата борса — нормализираме към държавно име (за Companies.Exchange) и ranked preference
    // (по-нисък Rank = по-предпочитана борса). Регион, който не съвпада с нищо тук, е извън обхвата
    // (Индия, Бразилия, OTC и т.н.) и се отхвърля изцяло.
    // Ползва се и за AV-евото "4. region" поле, и за Yahoo Finance search-а "exchDisp" поле (виж
    // SearchYahooAsync) — двата извикват различни низове за едни и същи борси (AV: "United States",
    // Yahoo: "NYSE"/"NASDAQ"), затова таблицата съдържа по няколко alias-а на ключова дума за общите борси.
    private static readonly (string RegionKeyword, int Rank, string ExchangeLabel)[] ExchangeRankings =
    [
        ("United States", 1, "United States"),
        ("NYSE", 1, "United States"),
        ("NASDAQ", 1, "United States"),
        ("Frankfurt", 2, "Germany"),
        ("XETRA", 2, "Germany"),
        ("United Kingdom", 3, "United Kingdom"),
        ("London", 3, "United Kingdom"),
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

    // Тикър, чиято последна реална сделка е по-стара от това, се третира като "мъртъв" — компанията
    // не се верифицира с него (виж IsTickerLiquidAsync). Открито на живо: Sealed Air Corporation
    // ("SEE") имаше валиден формат тикър, но нито Alpha Vantage (заседнал от 2026-04-09), нито Yahoo
    // ("symbol may be delisted") имаха актуални данни — компанията реално е спряла да се търгува.
    private const int StaleTickerThresholdDays = 14;

    private readonly AppDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IYahooFinanceService _yahooFinanceService;
    private readonly ILogger<TickerVerificationService> _logger;

    public TickerVerificationService(AppDbContext dbContext, HttpClient httpClient, IConfiguration configuration, IYahooFinanceService yahooFinanceService, ILogger<TickerVerificationService> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _configuration = configuration;
        _yahooFinanceService = yahooFinanceService;
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
            await EnsureSecTickersLoadedAsync(cancellationToken);

            // Ограничаваме на MaxCompaniesPerRun (15), за да не изчерпаме Alpha Vantage free tier
            // (25 заявки/ден) — компаниите, останали TickerVerified = false, се обработват в следващия run.
            var pendingCompanies = await _dbContext.Companies
                .Where(c => !c.TickerVerified)
                .OrderBy(c => c.CreatedAt)
                .Take(MaxCompaniesPerRun)
                .ToListAsync(cancellationToken);

            var verified = 0;
            var verifiedViaSec = 0;
            var calledAlphaVantage = false;
            var calledOpenFigi = false;

            foreach (var company in pendingCompanies)
            {
                try
                {
                    var secTicker = TryGetSecTicker(company.CompanyName);
                    if (secTicker is not null && await IsTickerLiquidAsync(secTicker, cancellationToken))
                    {
                        company.Ticker = secTicker;
                        company.Exchange = "United States";
                        company.TickerVerified = true;
                        company.TickerVerifiedAt = DateTime.UtcNow;
                        verified++;
                        verifiedViaSec++;
                        continue;
                    }

                    // SEC-ът покрива само US-listed компании — за останалите (европейски и др.) пада
                    // към Alpha Vantage, платено с rate limit delay между заявки.

                    // AI Analyzer-ът дава tickerHint (непроверено предположение) при създаването на
                    // компанията — търсим по него първо, защото символ обикновено дава по-прецизно
                    // (по-високо matchScore) съвпадение от fuzzy търсене по пълното име на компанията.
                    // Ако hint търсенето не намери нищо, падаме към името както преди.
                    RankedMatch? best = null;

                    if (!string.IsNullOrWhiteSpace(company.TickerHint))
                    {
                        if (calledAlphaVantage)
                        {
                            await Task.Delay(RateLimitDelayMs, cancellationToken);
                        }
                        calledAlphaVantage = true;

                        var hintCandidates = await SearchSymbolAsync(company.TickerHint, apiKey, cancellationToken);
                        best = PickBest(hintCandidates);
                    }

                    if (best is null)
                    {
                        if (calledAlphaVantage)
                        {
                            await Task.Delay(RateLimitDelayMs, cancellationToken);
                        }
                        calledAlphaVantage = true;

                        var nameCandidates = await SearchSymbolAsync(company.CompanyName, apiKey, cancellationToken);
                        best = PickBest(nameCandidates);
                    }

                    // Alpha Vantage не намери нищо — OpenFIGI е следващият опит: много по-широко
                    // глобално покритие от AV, но по-строг rate limit (5/мин без ключ).
                    if (best is null)
                    {
                        if (!string.IsNullOrWhiteSpace(company.TickerHint))
                        {
                            if (calledOpenFigi)
                            {
                                await Task.Delay(OpenFigiRateLimitDelayMs, cancellationToken);
                            }
                            calledOpenFigi = true;

                            best = await SearchOpenFigiAsync(company.TickerHint, cancellationToken);
                        }

                        // Търсене по bare ticker hint (напр. "HUH1V") може да върне само деривативи
                        // (futures/options на акцията), без нито един истински "Common Stock" резултат в
                        // страницата — потвърдено на живо за Huhtamaki. Пълното име на компанията търси
                        // по-точно и връща реалната акция сред резултатите. Падаме към него ако hint-ът
                        // не даде нищо приемливо.
                        if (best is null)
                        {
                            if (calledOpenFigi)
                            {
                                await Task.Delay(OpenFigiRateLimitDelayMs, cancellationToken);
                            }
                            calledOpenFigi = true;

                            best = await SearchOpenFigiAsync(company.CompanyName, cancellationToken);
                        }

                        // OpenFIGI връща "чист" symbol, но StockPriceService/OverboughtOversoldService
                        // ползват само Alpha Vantage за цени/RSI/MACD, а AV изисква суфиксиран формат
                        // за не-US акции (напр. "NESM.FRK"). Пробваме AV search с OpenFIGI тикъра като
                        // keyword — ако AV го разпознае, предпочитаме неговия цено-съвместим вариант.
                        if (best is not null)
                        {
                            if (calledAlphaVantage)
                            {
                                await Task.Delay(RateLimitDelayMs, cancellationToken);
                            }
                            calledAlphaVantage = true;

                            var avCrossCheck = PickBest(await SearchSymbolAsync(best.Symbol, apiKey, cancellationToken));
                            if (avCrossCheck is not null)
                            {
                                best = avCrossCheck;
                            }
                        }
                    }

                    // OpenFIGI също не намери нищо — Yahoo Finance-ото собствено (undocumented) search
                    // API е последният реален опит преди AI-hint fallback-а. Връща Yahoo-нативен symbol
                    // директно (напр. "1HUH.MI"), който вече е съвместим с YahooFinanceService за цени/
                    // RSI/MACD без нужда от AV cross-check стъпката по-горе (тя е специфична за OpenFIGI-
                    // евите symbol-и, които не са гарантирано в Yahoo-нотация).
                    if (best is null)
                    {
                        best = await SearchYahooAsync(company.TickerHint ?? company.CompanyName, cancellationToken);
                    }

                    if (best is not null && await IsTickerLiquidAsync(best.Symbol, cancellationToken))
                    {
                        company.Ticker = best.Symbol;
                        company.Exchange = best.ExchangeLabel;
                        company.TickerVerified = true;
                        company.TickerVerifiedAt = DateTime.UtcNow;
                        verified++;
                        continue;
                    }

                    // Нито един реален източник (SEC, AV, OpenFIGI) не намери нищо ЖИВО (или намереното
                    // се оказа "мъртъв" тикър без скорошни сделки, виж IsTickerLiquidAsync) — последен опит
                    // през SEC-овия OTC fallback.
                    var secOtcTicker = TryGetSecOtcTicker(company.CompanyName);
                    if (secOtcTicker is not null)
                    {
                        company.Ticker = secOtcTicker;
                        company.Exchange = "United States (OTC)";
                        company.TickerVerified = true;
                        company.TickerVerifiedAt = DateTime.UtcNow;
                        verified++;
                        verifiedViaSec++;
                        continue;
                    }

                    // Абсолютно нищо не потвърди тикър — ако AI Analyzer-ът е дал tickerHint при
                    // създаването на компанията, го използваме директно като последен опит, ясно
                    // маркиран като непотвърден (не идва от реален пазарен източник).
                    if (!string.IsNullOrWhiteSpace(company.TickerHint))
                    {
                        company.Ticker = company.TickerHint;
                        company.Exchange = "AI suggested (unverified)";
                        company.TickerVerified = true;
                        company.TickerVerifiedAt = DateTime.UtcNow;
                        verified++;
                        continue;
                    }

                    // Наистина нищо — маркираме верифицирана (за да не блокира опашката вечно за
                    // следващи run-ове), но с Ticker = null. UI-то показва изрично "No ticker found".
                    company.TickerVerified = true;
                    company.TickerVerifiedAt = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    // Един неуспешен symbol lookup (rate limit, мрежов проблем) не бива да проваля целия run —
                    // компанията си остава TickerVerified = false и ще се пробва пак следващия път.
                    _logger.LogWarning(ex, "Failed to verify ticker for {CompanyName}", company.CompanyName);
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Verified {Verified} ticker(s): {SecCount} via SEC (free), {AvCount} via Alpha Vantage",
                verified, verifiedViaSec, verified - verifiedViaSec);

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

            // LSE-ово "International Order Book" (IOB) — cross-listing сегмент за чужди акции. За разлика
            // от OTC ADR-и (реални, просто по-slabo ликвидни US сделки), IOB duplicate listings често изобщо
            // спират да търгуват — потвърдено на живо: Clariant "0QJS.LON" и Huhtamaki "0K9W.LON", и двете
            // без нито една сделка след 2026-07-17, докато основните им борси (Xetra/Milan) вървят нормално.
            // Пълно отхвърляне, не само демоция — по-добре компанията да остане unverified (следващ run
            // пробва пак, може с различен tier resultат) отколкото завинаги заклещена зад "цена = null".
            if (IsLikelyIobTicker(symbol))
            {
                continue;
            }

            // OTC ADR/foreign-ordinary тикъри (виж IsLikelyOtcAdrTicker) не са компанията реалната борса —
            // винаги предпочитаме истинска регионална борса пред тях. Но не ги отхвърляме напълно: ако AV
            // не върне НИЩО друго за тази компания, по-добре OTC цена, отколкото никаква (LastResortRank
            // е по-лош от всеки истински запис в ExchangeRankings, затова губи сравнението щом има алтернатива).
            var rank = IsLikelyOtcAdrTicker(symbol) ? LastResortOtcRank : exchange.Value.Rank;
            var label = IsLikelyOtcAdrTicker(symbol) ? $"{exchange.Value.Label} (OTC)" : exchange.Value.Label;

            candidates.Add(new RankedMatch(symbol, label, rank, matchScore));
        }

        return candidates;
    }

    private async Task<RankedMatch?> SearchOpenFigiAsync(string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenFigiSearchUrl)
        {
            Content = JsonContent.Create(new { query })
        };

        var openFigiApiKey = _configuration["OpenFigi:ApiKey"];
        if (!string.IsNullOrWhiteSpace(openFigiApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-OPENFIGI-APIKEY", openFigiApiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!payload.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            var ticker = item.TryGetProperty("ticker", out var tickerElement) ? tickerElement.GetString() : null;
            var marketSector = item.TryGetProperty("marketSector", out var sectorElement) ? sectorElement.GetString() : null;
            var securityType2 = item.TryGetProperty("securityType2", out var typeElement) ? typeElement.GetString() : null;
            var exchCode = item.TryGetProperty("exchCode", out var exchElement) ? exchElement.GetString() : null;

            // Futures/options/indices на дадена акция също имат marketSector="Equity" в OpenFIGI-евата
            // таксономия (объркващо) — потвърдено на живо: търсене по bare ticker hint (напр. "HUH1V" за
            // Huhtamaki) връща предимно "SINGLE STOCK FUTURE" контракти с тикъри като "HUH1V=1", "HUH1V=9"
            // (Bloomberg generic-future нотация), които марketSector филтърът сам по себе си пропускаше.
            // securityType2 == "Common Stock" изисква реалната акция, не деривативи.
            if (string.IsNullOrWhiteSpace(ticker) ||
                IsLikelyOtcAdrTicker(ticker) ||
                !string.Equals(marketSector, "Equity", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(securityType2, "Common Stock", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // OpenFIGI вече връща резултатите подредени по relevance — вземаме първия валиден equity
            // резултат, вместо да пресъздаваме собствен ranking. Rank/MatchScore тук са без значение
            // (никога не се сравняват с AV кандидати — OpenFIGI е отделен, самостоятелен tier).
            return new RankedMatch(ticker, DescribeOpenFigiExchange(exchCode), 0, 1m);
        }

        return null;
    }

    // Best-effort превод на OpenFIGI-специфичните exchange кодове към човешко име — покрива само
    // борсите, за които сме сигурни в кода; всичко друго показва суровия код вместо грешно предположение.
    private static string DescribeOpenFigiExchange(string? exchCode)
    {
        if (string.IsNullOrWhiteSpace(exchCode))
        {
            return "OpenFIGI";
        }

        return exchCode.ToUpperInvariant() switch
        {
            "US" => "United States",
            "LN" => "United Kingdom",
            "GR" or "GY" or "GF" => "Germany",
            "PA" => "France",
            "NA" => "Netherlands",
            "BB" => "Belgium",
            "IM" => "Italy",
            "SM" => "Spain",
            "SW" => "Switzerland",
            "SS" => "Sweden",
            _ => $"OpenFIGI ({exchCode})"
        };
    }

    private async Task<RankedMatch?> SearchYahooAsync(string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{YahooSearchApiUrl}?q={Uri.EscapeDataString(query)}");
        request.Headers.TryAddWithoutValidation("User-Agent", YahooUserAgent);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!payload.RootElement.TryGetProperty("quotes", out var quotes) || quotes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var candidates = new List<RankedMatch>();
        foreach (var item in quotes.EnumerateArray())
        {
            var symbol = item.TryGetProperty("symbol", out var symbolElement) ? symbolElement.GetString() : null;
            var quoteType = item.TryGetProperty("quoteType", out var typeElement) ? typeElement.GetString() : null;
            var exchDisp = item.TryGetProperty("exchDisp", out var exchElement) ? exchElement.GetString() : null;

            // quoteType филтрира индекси/фючърси/опции/ETF-и и т.н. — Yahoo-то, за разлика от OpenFIGI,
            // прави тази разлика изрично видима вместо да ги смесва под общ "Equity" sector.
            if (string.IsNullOrWhiteSpace(symbol) || !string.Equals(quoteType, "EQUITY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // "International Orderbook" (LSE-ово cross-listing за чужди акции) е технически валиден
            // Equity резултат, но на практика почти нелинкидиран duplicate listing — потвърдено на живо
            // за Huhtamaki (0K9W.IL/.LON): последна реална сделка беше от преди седмици, докато
            // основната ѝ борса (Milan/Helsinki) търгуваше нормално всеки ден. Отхвърляме изрично,
            // вместо да разчитаме случайно на по-добър кандидат по-надолу в списъка.
            if (exchDisp is not null && exchDisp.Contains("International Orderbook", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var exchange = ClassifyExchange(exchDisp);
            if (exchange is null)
            {
                // Извън обхвата на предпочитаните борси (напр. Индия NSE/BSE, Бразилия São Paulo) —
                // отхвърляме напълно, вместо да гадаем ранг.
                continue;
            }

            var rank = IsLikelyOtcAdrTicker(symbol) ? LastResortOtcRank : exchange.Value.Rank;
            var label = IsLikelyOtcAdrTicker(symbol) ? $"{exchange.Value.Label} (OTC)" : exchange.Value.Label;
            candidates.Add(new RankedMatch(symbol, label, rank, 1m));
        }

        return PickBest(candidates);
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

    private async Task EnsureSecTickersLoadedAsync(CancellationToken cancellationToken)
    {
        if (_secTickersByNormalizedName is not null && DateTime.UtcNow - _secTickersLoadedAt < SecTickerCacheDuration)
        {
            return;
        }

        await SecTickerLoadLock.WaitAsync(cancellationToken);
        try
        {
            if (_secTickersByNormalizedName is not null && DateTime.UtcNow - _secTickersLoadedAt < SecTickerCacheDuration)
            {
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, SecCompanyTickersUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", SecUserAgent);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var otcMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in payload.RootElement.EnumerateObject())
            {
                var ticker = entry.Value.TryGetProperty("ticker", out var tickerElement) ? tickerElement.GetString() : null;
                var title = entry.Value.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;

                if (string.IsNullOrWhiteSpace(ticker) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                // SEC-ът е full-text регистър на всеки SEC filer, включително чужди компании, които
                // се търгуват в US само като OTC ADR/foreign-ordinary — не тяхната реална борса. Тези
                // тикъри следват конвенция (5 букви, завършващи на Y/F) — пазим ги отделно като
                // last-resort fallback, ако и Alpha Vantage не намери реална регионална борса.
                var target = IsLikelyOtcAdrTicker(ticker) ? otcMap : map;

                // Първо срещане печели — SEC подрежда по CIK, не по значимост, но дублирани
                // нормализирани имена са рядкост.
                target.TryAdd(NormalizeCompanyName(title), ticker);
            }

            _secTickersByNormalizedName = map;
            _secOtcTickersByNormalizedName = otcMap;
            _secTickersLoadedAt = DateTime.UtcNow;
        }
        finally
        {
            SecTickerLoadLock.Release();
        }
    }

    private static string? TryGetSecTicker(string companyName)
    {
        if (_secTickersByNormalizedName is null)
        {
            return null;
        }

        return _secTickersByNormalizedName.TryGetValue(NormalizeCompanyName(companyName), out var ticker)
            ? ticker
            : null;
    }

    private static string? TryGetSecOtcTicker(string companyName)
    {
        if (_secOtcTickersByNormalizedName is null)
        {
            return null;
        }

        return _secOtcTickersByNormalizedName.TryGetValue(NormalizeCompanyName(companyName), out var ticker)
            ? ticker
            : null;
    }

    // OTC Markets конвенция: 5-буквени тикъри, завършващи на Y (ADR) или F (foreign ordinary),
    // обозначават чужда компания, търгувана в US само като OTC quote — не реалната ѝ борса.
    // Пример: BASF SE се търгува основно на XETRA, но SEC/Alpha Vantage връщат и "BASFY" (US OTC ADR).
    // Сортираме по (1) exchange rank, (2) matchScore DESC — предпочитаме резултат от по-желана
    // борса дори при по-нисък matchScore, вместо просто най-добрия overall match. OTC резултати
    // вече влизат тук с LastResortOtcRank, така че печелят само ако няма истинска борса.
    private static RankedMatch? PickBest(List<RankedMatch> candidates) =>
        candidates.OrderBy(c => c.ExchangeRank).ThenByDescending(c => c.MatchScore).FirstOrDefault();

    private static bool IsLikelyOtcAdrTicker(string ticker) =>
        ticker.Length == 5 && (ticker[^1] == 'Y' || ticker[^1] == 'F') && ticker.All(char.IsAsciiLetter);

    // LSE International Order Book тикъри имат разпознаваем формат: започват с цифра, AV суфиксира ги с
    // ".LON" (напр. "0QJS.LON", "0K9W.LON") — виж коментара при употребата за защо ги отхвърляме напълно.
    private static readonly Regex IobTickerPattern = new(@"^\d.*\.LON$", RegexOptions.Compiled);

    private static bool IsLikelyIobTicker(string ticker) => IobTickerPattern.IsMatch(ticker);

    // Проверява, че тикърът реално се търгува скоро (не просто "форматът изглежда правилно"). IsLikelyIobTicker
    // хваща един конкретен, разпознаваем по формат случай (LSE IOB) — но "мъртви" тикъри могат да изглеждат
    // напълно нормално (напр. "SEE" за Sealed Air — валиден 3-буквен NYSE формат, компанията просто реално е
    // спряла да се търгува). GetDailyClosesAsync вече пропуска null close-ове (виж YahooFinanceService), затова
    // най-скорошният КЛЮЧ в резултата директно е датата на последната реална сделка.
    private async Task<bool> IsTickerLiquidAsync(string ticker, CancellationToken cancellationToken)
    {
        var series = await _yahooFinanceService.GetDailyClosesAsync(ticker, cancellationToken);
        if (series is null || series.Count == 0)
        {
            return false;
        }

        var mostRecentTradeDate = series.Keys.Max();
        return (DateTime.UtcNow.Date - mostRecentTradeDate).TotalDays <= StaleTickerThresholdDays;
    }

    private static string NormalizeCompanyName(string name)
    {
        var cleaned = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9 ]", " ");
        var words = cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !CompanySuffixes.Contains(word));

        return string.Join(' ', words);
    }

    private sealed record RankedMatch(string Symbol, string ExchangeLabel, int ExchangeRank, decimal MatchScore);
}
