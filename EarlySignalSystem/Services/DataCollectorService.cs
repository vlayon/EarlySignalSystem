using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EarlySignalSystem.Data;
using EarlySignalSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace EarlySignalSystem.Services;

public class DataCollectorService : IDataCollectorService
{
    private const string EurLexRssUrl = "https://eur-lex.europa.eu/EN/display-feed.rss?rssId=222";
    private const string EurLexSource = "EUR-Lex";
    private const string LegislationSignalType = "Legislation";

    private readonly AppDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DataCollectorService> _logger;

    public DataCollectorService(AppDbContext dbContext, HttpClient httpClient, ILogger<DataCollectorService> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<int> CollectEurLexSignalsAsync(CancellationToken cancellationToken = default)
    {
        var runLog = new RunLog
        {
            StartedAt = DateTime.UtcNow,
            Status = "Running",
            JobName = EurLexSource
        };
        _dbContext.RunLogs.Add(runLog);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var collected = 0;
        try
        {
            var items = await FetchFeedItemsAsync(cancellationToken);

            var existingLinks = await _dbContext.Signals
                .Where(s => s.Source == EurLexSource)
                .Select(s => s.SourceUrl)
                .ToHashSetAsync(cancellationToken);

            foreach (var item in items)
            {
                if (existingLinks.Contains(item.Link))
                {
                    continue;
                }

                _dbContext.Signals.Add(new Signal
                {
                    Source = EurLexSource,
                    SignalType = LegislationSignalType,
                    SourceUrl = item.Link,
                    Title = item.Title,
                    RawContent = item.Description,
                    PublishedAt = item.PublishedAt,
                    CollectedAt = DateTime.UtcNow,
                    Processed = false,
                    RunLogId = runLog.Id
                });

                existingLinks.Add(item.Link);
                collected++;
            }

            runLog.Status = "Completed";
            runLog.SignalsCollected = collected;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect EUR-Lex signals");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        return collected;
    }

    private async Task<List<EurLexFeedItem>> FetchFeedItemsAsync(CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync(EurLexRssUrl, cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

        var items = new List<EurLexFeedItem>();
        foreach (var item in document.Descendants("item"))
        {
            var title = item.Element("title")?.Value.Trim() ?? string.Empty;
            var link = item.Element("link")?.Value.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
            {
                continue;
            }

            var description = item.Element("description")?.Value.Trim();
            var pubDateRaw = item.Element("pubDate")?.Value;

            var publishedAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(pubDateRaw) &&
                DateTimeOffset.TryParse(pubDateRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                publishedAt = parsed.UtcDateTime;
            }

            items.Add(new EurLexFeedItem(title, link, description, publishedAt));
        }

        return items;
    }

    private sealed record EurLexFeedItem(string Title, string Link, string? Description, DateTime PublishedAt);

    private const string SecEdgarSource = "SEC-EDGAR";
    private const string InsiderBuyingSignalType = "InsiderBuying";
    private const string SecEdgarFeedUrl = "https://www.sec.gov/cgi-bin/browse-edgar?action=getcurrent&type=4&dateb=&owner=include&count=40&search_text=&output=atom";
    private const string SecEdgarUserAgent = "EarlySignalSystem research@earlysignalsystem.local";

    public async Task<int> CollectSecEdgarSignalsAsync(CancellationToken cancellationToken = default)
    {
        var runLog = new RunLog
        {
            StartedAt = DateTime.UtcNow,
            Status = "Running",
            JobName = SecEdgarSource
        };
        _dbContext.RunLogs.Add(runLog);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var collected = 0;
        try
        {
            var filings = await FetchSecEdgarFilingRefsAsync(cancellationToken);

            var existingLinks = await _dbContext.Signals
                .Where(s => s.Source == SecEdgarSource)
                .Select(s => s.SourceUrl)
                .ToHashSetAsync(cancellationToken);

            foreach (var filing in filings)
            {
                if (existingLinks.Contains(filing.IndexUrl))
                {
                    continue;
                }

                existingLinks.Add(filing.IndexUrl);

                SecEdgarPurchase? purchase;
                try
                {
                    purchase = await FetchSecEdgarPurchaseAsync(filing.IndexUrl, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Отделен per-filing catch: една зле форматирана SEC XML не бива да проваля целия run.
                    _logger.LogWarning(ex, "Failed to parse SEC EDGAR filing {IndexUrl}", filing.IndexUrl);
                    continue;
                }

                if (purchase is null)
                {
                    continue;
                }

                _dbContext.Signals.Add(new Signal
                {
                    Source = SecEdgarSource,
                    SignalType = InsiderBuyingSignalType,
                    SourceUrl = filing.IndexUrl,
                    Title = $"{purchase.ReportingOwnerName} ({purchase.Role}) bought {purchase.TotalShares:N0} shares of {purchase.IssuerName}",
                    RawContent = $"Issuer: {purchase.IssuerName} ({purchase.Ticker}); Insider: {purchase.ReportingOwnerName}; Shares: {purchase.TotalShares:N0}; Avg price: {purchase.WeightedAveragePrice:0.00}; Transaction date: {purchase.TransactionDate:yyyy-MM-dd}",
                    Ticker = purchase.Ticker,
                    PublishedAt = purchase.TransactionDate,
                    CollectedAt = DateTime.UtcNow,
                    Processed = false,
                    RunLogId = runLog.Id
                });

                collected++;
            }

            runLog.Status = "Completed";
            runLog.SignalsCollected = collected;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect SEC EDGAR signals");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        return collected;
    }

    private async Task<List<SecEdgarFilingRef>> FetchSecEdgarFilingRefsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SecEdgarFeedUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", SecEdgarUserAgent);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

        XNamespace atom = "http://www.w3.org/2005/Atom";
        var filings = new List<SecEdgarFilingRef>();
        var seenAccessionNumbers = new HashSet<string>();

        foreach (var entry in document.Descendants(atom + "entry"))
        {
            var link = entry.Element(atom + "link")?.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(link))
            {
                continue;
            }

            // Всяко подаване се появява веднъж под CIK-а на issuer-а и веднъж под CIK-а на reporting owner-а —
            // различни URL адреси за едно и също подаване, затова дедупликираме по accession number, не по URL.
            var accessionMatch = Regex.Match(link, @"(?<accession>\d{10}-\d{2}-\d{6})-index\.htm", RegexOptions.IgnoreCase);
            var dedupKey = accessionMatch.Success ? accessionMatch.Groups["accession"].Value : link;
            if (!seenAccessionNumbers.Add(dedupKey))
            {
                continue;
            }

            var updatedRaw = entry.Element(atom + "updated")?.Value;
            var filedAt = DateTimeOffset.TryParse(updatedRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed.UtcDateTime
                : DateTime.UtcNow;

            filings.Add(new SecEdgarFilingRef(link, filedAt));
        }

        return filings;
    }

    private async Task<SecEdgarPurchase?> FetchSecEdgarPurchaseAsync(string indexUrl, CancellationToken cancellationToken)
    {
        using var indexRequest = new HttpRequestMessage(HttpMethod.Get, indexUrl);
        indexRequest.Headers.TryAddWithoutValidation("User-Agent", SecEdgarUserAgent);
        using var indexResponse = await _httpClient.SendAsync(indexRequest, cancellationToken);
        indexResponse.EnsureSuccessStatusCode();
        var indexHtml = await indexResponse.Content.ReadAsStringAsync(cancellationToken);

        var xmlUrl = ExtractOwnershipXmlUrl(indexHtml, indexUrl);
        if (xmlUrl is null)
        {
            return null;
        }

        using var xmlRequest = new HttpRequestMessage(HttpMethod.Get, xmlUrl);
        xmlRequest.Headers.TryAddWithoutValidation("User-Agent", SecEdgarUserAgent);
        using var xmlResponse = await _httpClient.SendAsync(xmlRequest, cancellationToken);
        xmlResponse.EnsureSuccessStatusCode();

        await using var xmlStream = await xmlResponse.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(xmlStream, LoadOptions.None, cancellationToken);

        // aff10b5One = "This transaction was made pursuant to a Rule 10b5-1(c) trading plan" — тези са автоматични, не дискреционни покупки.
        var isRule10b51 = document.Root?.Element("aff10b5One")?.Value.Trim() == "1";
        if (isRule10b51)
        {
            return null;
        }

        var issuerName = document.Root?.Element("issuer")?.Element("issuerName")?.Value.Trim() ?? string.Empty;
        var ticker = document.Root?.Element("issuer")?.Element("issuerTradingSymbol")?.Value.Trim();
        var ownerName = document.Root?.Element("reportingOwner")?.Element("reportingOwnerId")?.Element("rptOwnerName")?.Value.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(issuerName) || string.IsNullOrWhiteSpace(ownerName))
        {
            return null;
        }

        var relationship = document.Root?.Element("reportingOwner")?.Element("reportingOwnerRelationship");
        var role = DetermineInsiderRole(relationship);

        var purchaseTransactions = document.Root?
            .Element("nonDerivativeTable")?
            .Elements("nonDerivativeTransaction")
            .Where(t => t.Element("transactionCoding")?.Element("transactionCode")?.Value.Trim() == "P"
                && t.Element("transactionAmounts")?.Element("transactionAcquiredDisposedCode")?.Element("value")?.Value.Trim() == "A")
            .ToList() ?? [];

        if (purchaseTransactions.Count == 0)
        {
            return null;
        }

        var totalShares = 0m;
        var totalCost = 0m;
        var transactionDate = DateTime.UtcNow;

        foreach (var transaction in purchaseTransactions)
        {
            var shares = ParseDecimal(transaction.Element("transactionAmounts")?.Element("transactionShares")?.Element("value")?.Value);
            var price = ParseDecimal(transaction.Element("transactionAmounts")?.Element("transactionPricePerShare")?.Element("value")?.Value);
            var dateRaw = transaction.Element("transactionDate")?.Element("value")?.Value;

            if (DateTime.TryParse(dateRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                transactionDate = parsedDate;
            }

            totalShares += shares;
            totalCost += shares * price;
        }

        if (totalShares <= 0)
        {
            return null;
        }

        return new SecEdgarPurchase(issuerName, ticker, ownerName, role, totalShares, totalCost / totalShares, transactionDate);
    }

    // officerTitle е свободен текст ("Chief Executive Officer", "EVP & General Counsel"...) — нормализираме
    // към кратки категории, за да могат Insiders.razor филтрите (By Role) да работят с точно съвпадение.
    private static string DetermineInsiderRole(XElement? relationship)
    {
        if (relationship is null)
        {
            return "Insider";
        }

        var isOfficer = relationship.Element("isOfficer")?.Value.Trim() == "1";
        var officerTitle = relationship.Element("officerTitle")?.Value.Trim();

        if (isOfficer && !string.IsNullOrWhiteSpace(officerTitle))
        {
            if (officerTitle.Contains("chief executive", StringComparison.OrdinalIgnoreCase))
            {
                return "CEO";
            }

            if (officerTitle.Contains("chief financial", StringComparison.OrdinalIgnoreCase))
            {
                return "CFO";
            }

            if (officerTitle.Contains("chief operating", StringComparison.OrdinalIgnoreCase))
            {
                return "COO";
            }

            return officerTitle;
        }

        if (relationship.Element("isDirector")?.Value.Trim() == "1")
        {
            return "Director";
        }

        if (relationship.Element("isTenPercentOwner")?.Value.Trim() == "1")
        {
            return "10% Owner";
        }

        return "Insider";
    }

    private static decimal ParseDecimal(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0m;

    private static string? ExtractOwnershipXmlUrl(string indexHtml, string indexUrl)
    {
        var candidate = Regex.Matches(indexHtml, "href=\"(?<href>[^\"]+\\.xml)\"", RegexOptions.IgnoreCase)
            .Select(m => m.Groups["href"].Value)
            .FirstOrDefault(href => !href.Contains("/xslF345X", StringComparison.OrdinalIgnoreCase));

        return candidate is null ? null : new Uri(new Uri(indexUrl), candidate).ToString();
    }

    private sealed record SecEdgarFilingRef(string IndexUrl, DateTime FiledAt);

    private sealed record SecEdgarPurchase(string IssuerName, string? Ticker, string ReportingOwnerName, string Role, decimal TotalShares, decimal WeightedAveragePrice, DateTime TransactionDate);

    private const string TedSource = "TED";
    private const string GovernmentContractSignalType = "GovernmentContract";
    private const string TedApiUrl = "https://api.ted.europa.eu/v3/notices/search";
    private const decimal MinContractValueEur = 1_000_000m;

    public async Task<int> CollectTedSignalsAsync(CancellationToken cancellationToken = default)
    {
        var runLog = new RunLog
        {
            StartedAt = DateTime.UtcNow,
            Status = "Running",
            JobName = TedSource
        };
        _dbContext.RunLogs.Add(runLog);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var collected = 0;
        try
        {
            var notices = await FetchTedNoticesAsync(cancellationToken);

            var existingLinks = await _dbContext.Signals
                .Where(s => s.Source == TedSource)
                .Select(s => s.SourceUrl)
                .ToHashSetAsync(cancellationToken);

            foreach (var notice in notices)
            {
                var sourceUrl = notice.NoticeUrl ?? $"https://ted.europa.eu/en/notice/{notice.NoticeId}";
                if (existingLinks.Contains(sourceUrl) || notice.ValueEur is null || notice.ValueEur < MinContractValueEur)
                {
                    continue;
                }

                _dbContext.Signals.Add(new Signal
                {
                    Source = TedSource,
                    SignalType = GovernmentContractSignalType,
                    SourceUrl = sourceUrl,
                    Title = notice.Title,
                    RawContent = $"Value: {notice.ValueEur:N0} EUR; Country: {notice.Country ?? "n/a"}",
                    PublishedAt = notice.PublishedAt,
                    CollectedAt = DateTime.UtcNow,
                    Processed = false,
                    RunLogId = runLog.Id
                });

                existingLinks.Add(sourceUrl);
                collected++;
            }

            runLog.Status = "Completed";
            runLog.SignalsCollected = collected;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect TED signals");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        return collected;
    }

    private async Task<List<TedNotice>> FetchTedNoticesAsync(CancellationToken cancellationToken)
    {
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var requestBody = new
        {
            query = $"PD>={yesterday}",
            fields = new[] { "ND", "TI", "CY", "DT", "TV" },
            page = 1,
            limit = 100,
            paginationMode = "PAGE_NUMBER"
        };

        using var response = await _httpClient.PostAsJsonAsync(TedApiUrl, requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var notices = new List<TedNotice>();
        if (!payload.RootElement.TryGetProperty("notices", out var noticesElement) || noticesElement.ValueKind != JsonValueKind.Array)
        {
            return notices;
        }

        foreach (var notice in noticesElement.EnumerateArray())
        {
            var noticeId = GetTedScalarValue(notice, "ND");
            if (string.IsNullOrWhiteSpace(noticeId))
            {
                continue;
            }

            var title = GetTedTitle(notice) ?? string.Empty;
            var country = GetTedScalarValue(notice, "CY");
            var publishedAt = DateTimeOffset.TryParse(GetTedScalarValue(notice, "DT"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                ? parsedDate.UtcDateTime
                : DateTime.UtcNow;
            var valueEur = ParseTedValue(GetTedScalarValue(notice, "TV"));
            var noticeUrl = GetTedNoticeUrl(notice);

            notices.Add(new TedNotice(noticeId, title, valueEur, country, publishedAt, noticeUrl));
        }

        return notices;
    }

    // ND/CY/DT/TV идват като скалар или еднoелементен масив в зависимост от полето — четем първия елемент, ако е масив.
    private static string? GetTedScalarValue(JsonElement notice, string propertyName)
    {
        if (!notice.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            value = value.EnumerateArray().FirstOrDefault();
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    // TI не е масив, а обект keyed по ISO 639-2 езиков код (напр. "eng", "fra"...) — предпочитаме английски,
    // иначе вземаме първия наличен превод.
    private static string? GetTedTitle(JsonElement notice)
    {
        if (!notice.TryGetProperty("TI", out var titles) || titles.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (titles.TryGetProperty("eng", out var englishTitle) && englishTitle.ValueKind == JsonValueKind.String)
        {
            return englishTitle.GetString();
        }

        return titles.EnumerateObject().FirstOrDefault().Value.GetString();
    }

    // Всеки notice носи собствени permalink-ове (links.html.ENG) — по-надеждни от ръчно конструиран URL.
    private static string? GetTedNoticeUrl(JsonElement notice)
    {
        if (notice.TryGetProperty("links", out var links) &&
            links.TryGetProperty("html", out var html) &&
            html.TryGetProperty("ENG", out var englishHtmlLink) &&
            englishHtmlLink.ValueKind == JsonValueKind.String)
        {
            return englishHtmlLink.GetString();
        }

        return null;
    }

    private static decimal? ParseTedValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = Regex.Replace(raw, @"[^\d.,]", string.Empty).Replace(",", string.Empty);
        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private sealed record TedNotice(string NoticeId, string Title, decimal? ValueEur, string? Country, DateTime PublishedAt, string? NoticeUrl);

    private const string OecdSource = "OECD";
    private const string CliTurningPointSignalType = "CliTurningPoint";
    // OECD.SDD.STES, "Composite leading indicators" (DF_CLI) — амплитудно-коригиран (AA), месечен (M) индекс.
    // 100 = дългосрочен тренд на растеж; CLI е проектиран да сигнализира обръщания на икономическия цикъл
    // 4-8 месеца преди да се видят в реалните данни (BNP и т.н.) — точно "преди пазара" философията на системата.
    // Старата DF_TABLE12_IDC (годишен government deficit/surplus) беше премахната: годишните данни не дават
    // никакъв "нов" сигнал month-to-month. DF_CLI версия 4.0 връща празни observations на sdmx.oecd.org
    // (маркирана "NonProductionDataflow") — 4.1 е активната версия, проверено на живо.
    // Държавите покриват основните борси от Ticker Verification pipeline-а (US/UK/DE/FR/IT/ES/JP/CN).
    private const string OecdCliCountries = "USA+DEU+FRA+GBR+JPN+CHN+ITA+ESP";
    private const string OecdCliApiUrlBase = "https://sdmx.oecd.org/public/rest/data/OECD.SDD.STES,DSD_STES@DF_CLI,4.1/" + OecdCliCountries + ".M.LI...AA...";
    private const decimal CliTrendThreshold = 100m;

    public async Task<int> CollectOecdSignalsAsync(CancellationToken cancellationToken = default)
    {
        var runLog = new RunLog
        {
            StartedAt = DateTime.UtcNow,
            Status = "Running",
            JobName = OecdSource
        };
        _dbContext.RunLogs.Add(runLog);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var collected = 0;
        try
        {
            var crossings = await FetchOecdCliTurningPointsAsync(cancellationToken);

            var existingLinks = await _dbContext.Signals
                .Where(s => s.Source == OecdSource)
                .Select(s => s.SourceUrl)
                .ToHashSetAsync(cancellationToken);

            foreach (var crossing in crossings)
            {
                if (existingLinks.Contains(crossing.SourceUrl))
                {
                    continue;
                }

                var direction = crossing.LatestValue >= CliTrendThreshold ? "above" : "below";
                var outlook = crossing.LatestValue >= CliTrendThreshold ? "above-trend growth ahead" : "below-trend slowdown ahead";

                _dbContext.Signals.Add(new Signal
                {
                    Source = OecdSource,
                    SignalType = CliTurningPointSignalType,
                    SourceUrl = crossing.SourceUrl,
                    Title = $"{crossing.CountryName} OECD Composite Leading Indicator crosses {direction} 100 — signals {outlook}",
                    RawContent = $"Previous: {crossing.PreviousValue:F2} ({crossing.PreviousPeriod}); Latest: {crossing.LatestValue:F2} ({crossing.LatestPeriod})",
                    PublishedAt = DateTime.UtcNow,
                    CollectedAt = DateTime.UtcNow,
                    Processed = false,
                    RunLogId = runLog.Id
                });

                existingLinks.Add(crossing.SourceUrl);
                collected++;
            }

            runLog.Status = "Completed";
            runLog.SignalsCollected = collected;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OECD collector failed");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return 0;
        }

        return collected;
    }

    private async Task<List<OecdCliCrossing>> FetchOecdCliTurningPointsAsync(CancellationToken cancellationToken)
    {
        // Последните 8 месеца стигат за previous/latest сравнение дори при държави с забавена публикация
        // (напр. Китай често изостава 1-2 месеца спрямо US/EU).
        var startPeriod = DateTime.UtcNow.AddMonths(-8).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var url = $"{OecdCliApiUrlBase}?startPeriod={startPeriod}&dimensionAtObservation=AllDimensions";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.sdmx.data+json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ParseOecdCliCrossings(payload, url);
    }

    // dimensionAtObservation=AllDimensions връща плосък observations dictionary, ключуван с двоеточие-разделени
    // индекси в реда на dimensions.observation (REF_AREA, FREQ, MEASURE, UNIT_MEASURE, ACTIVITY, ADJUSTMENT,
    // TRANSFORMATION, TIME_HORIZ, METHODOLOGY, TIME_PERIOD) — само REF_AREA и TIME_PERIOD варират реално тук,
    // защото останалите dimensions са constrained до по 1 стойност от заявката (LI/AA/M и т.н.).
    private static List<OecdCliCrossing> ParseOecdCliCrossings(JsonDocument payload, string sourceUrl)
    {
        var results = new List<OecdCliCrossing>();

        var root = payload.RootElement.GetProperty("data");
        var dataSets = root.GetProperty("dataSets");
        if (dataSets.GetArrayLength() == 0)
        {
            return results;
        }

        var observationDims = root.GetProperty("structures")[0].GetProperty("dimensions").GetProperty("observation");
        var refAreaPos = -1;
        var timePeriodPos = -1;
        JsonElement refAreaValues = default;
        JsonElement timePeriodValues = default;

        for (var i = 0; i < observationDims.GetArrayLength(); i++)
        {
            var dim = observationDims[i];
            var id = dim.GetProperty("id").GetString();
            if (id == "REF_AREA")
            {
                refAreaPos = i;
                refAreaValues = dim.GetProperty("values");
            }
            else if (id == "TIME_PERIOD")
            {
                timePeriodPos = i;
                timePeriodValues = dim.GetProperty("values");
            }
        }

        if (refAreaPos < 0 || timePeriodPos < 0)
        {
            return results;
        }

        // country code -> list of (period, value), заредено от observations dictionary-я преди сортиране.
        var byCountry = new Dictionary<string, List<(string Period, decimal Value)>>();
        var countryNames = new Dictionary<string, string>();

        if (!dataSets[0].TryGetProperty("observations", out var observations))
        {
            return results;
        }

        foreach (var obs in observations.EnumerateObject())
        {
            if (obs.Value[0].ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var indices = obs.Name.Split(':');
            if (refAreaPos >= indices.Length || timePeriodPos >= indices.Length)
            {
                continue;
            }

            if (!int.TryParse(indices[refAreaPos], out var refAreaIndex) || !int.TryParse(indices[timePeriodPos], out var timePeriodIndex))
            {
                continue;
            }

            var countryElement = refAreaValues[refAreaIndex];
            var countryCode = countryElement.GetProperty("id").GetString() ?? "?";
            var countryName = countryElement.GetProperty("name").GetString() ?? countryCode;
            var period = timePeriodValues[timePeriodIndex].GetProperty("id").GetString() ?? timePeriodIndex.ToString(CultureInfo.InvariantCulture);
            var value = obs.Value[0].GetDecimal();

            if (!byCountry.TryGetValue(countryCode, out var series))
            {
                series = [];
                byCountry[countryCode] = series;
            }

            series.Add((period, value));
            countryNames[countryCode] = countryName;
        }

        foreach (var (countryCode, series) in byCountry)
        {
            var ordered = series.OrderBy(s => s.Period, StringComparer.Ordinal).ToList();
            if (ordered.Count < 2)
            {
                continue;
            }

            var previous = ordered[^2];
            var latest = ordered[^1];

            var crossedAbove = previous.Value < CliTrendThreshold && latest.Value >= CliTrendThreshold;
            var crossedBelow = previous.Value >= CliTrendThreshold && latest.Value < CliTrendThreshold;
            if (!crossedAbove && !crossedBelow)
            {
                continue;
            }

            results.Add(new OecdCliCrossing(
                countryCode,
                countryNames[countryCode],
                previous.Value,
                previous.Period,
                latest.Value,
                latest.Period,
                $"{sourceUrl}#{countryCode}:{latest.Period}"));
        }

        return results;
    }

    private sealed record OecdCliCrossing(string CountryCode, string CountryName, decimal PreviousValue, string PreviousPeriod, decimal LatestValue, string LatestPeriod, string SourceUrl);

    private const string AmfSource = "AMF-France";
    private const string ShortInterestDeclineSignalType = "ShortInterestDecline";
    // ESMA-то си премести net-short-position данните към CAPTCHA-защитен registers портал (registers.esma.europa.eu) —
    // потвърдено на живо (2026-08-05): и главната търсачка, и всеки export извикват captcha endpoint, автоматичен
    // достъп е невъзможен без официален API ключ. Вместо pan-EU агрегатора минаваме на AMF France (National Competent
    // Authority под същия EU Short Selling Regulation) — публикуват same-type net-short-position данни свободно през
    // data.gouv.fr, без CAPTCHA, обновявано дневно. По-тесен обхват (само FR-листнати емитенти), но реален и работещ.
    private const string AmfCsvUrl = "https://www.data.gouv.fr/api/1/datasets/r/c2539d1c-8531-4937-9cba-3bd8e9786cc5";
    private const decimal ShortInterestDeclineThresholdPoints = 1m;
    private const string AmfSnapshotPrefix = "AMF_SNAPSHOT:";

    public async Task<int> CollectAmfSignalsAsync(CancellationToken cancellationToken = default)
    {
        var runLog = new RunLog
        {
            StartedAt = DateTime.UtcNow,
            Status = "Running",
            JobName = AmfSource
        };
        _dbContext.RunLogs.Add(runLog);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var collected = 0;
        try
        {
            var positions = await FetchAmfShortPositionsAsync(cancellationToken);

            // AMF файлът дава само текущ snapshot (не времеви редове), затова пазим предходния snapshot в RunLog.Notes,
            // за да можем да сравняваме спад спрямо предходния run — само декларираните спадове стават Signal записи.
            var previousRunLog = await _dbContext.RunLogs
                .Where(r => r.Notes != null && r.Notes.StartsWith(AmfSnapshotPrefix))
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var previousPercents = previousRunLog is not null
                ? JsonSerializer.Deserialize<Dictionary<string, decimal>>(previousRunLog.Notes![AmfSnapshotPrefix.Length..]) ?? new Dictionary<string, decimal>()
                : new Dictionary<string, decimal>();

            var existingLinks = await _dbContext.Signals
                .Where(s => s.Source == AmfSource)
                .Select(s => s.SourceUrl)
                .ToHashSetAsync(cancellationToken);

            foreach (var position in positions)
            {
                if (!previousPercents.TryGetValue(position.Isin, out var previousPercent))
                {
                    continue;
                }

                var decline = previousPercent - position.NetShortPositionPercent;
                var sourceUrl = $"{AmfCsvUrl}#{position.Isin}:{position.PositionDate:yyyy-MM-dd}";
                if (decline <= ShortInterestDeclineThresholdPoints || existingLinks.Contains(sourceUrl))
                {
                    continue;
                }

                _dbContext.Signals.Add(new Signal
                {
                    Source = AmfSource,
                    SignalType = ShortInterestDeclineSignalType,
                    SourceUrl = sourceUrl,
                    Title = $"{position.IssuerName} short interest fell {decline:0.0} pts",
                    RawContent = $"Previous: {previousPercent:0.00}%; Latest: {position.NetShortPositionPercent:0.00}%; Position date: {position.PositionDate:yyyy-MM-dd}",
                    Ticker = position.Isin, // Signal няма отделно ISIN поле — преизползваме Ticker за идентификатора на емитента.
                    PublishedAt = position.PositionDate,
                    CollectedAt = DateTime.UtcNow,
                    Processed = false,
                    RunLogId = runLog.Id
                });

                existingLinks.Add(sourceUrl);
                collected++;
            }

            runLog.Status = "Completed";
            runLog.SignalsCollected = collected;
            runLog.CompletedAt = DateTime.UtcNow;
            runLog.Notes = AmfSnapshotPrefix + JsonSerializer.Serialize(positions.ToDictionary(p => p.Isin, p => p.NetShortPositionPercent));
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AMF collector failed");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return 0;
        }

        return collected;
    }

    private async Task<List<AmfShortPosition>> FetchAmfShortPositionsAsync(CancellationToken cancellationToken)
    {
        var csv = await _httpClient.GetStringAsync(AmfCsvUrl, cancellationToken);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return [];
        }

        var headers = SplitCsvLine(lines[0], ';');
        var holderIndex = FindColumnIndex(headers, "Detenteur de la position courte nette");
        var issuerIndex = FindColumnIndex(headers, "Emetteur / issuer");
        var isinIndex = FindColumnIndex(headers, "code ISIN");
        var dateIndex = FindColumnIndex(headers, "Date de debut position");
        var percentIndex = FindColumnIndex(headers, "Ratio");
        // Празна "Date de fin de publication position" означава позицията никога не е затваряна, НЕ че редът е
        // последният/актуалният — файлът е пълен history log, всяка промяна на ratio-то е отделен ред без end-date,
        // само пълно затваряне на позицията слага end-date. Затова взимаме само реда с най-скорошна
        // "Date de debut position" за всяка двойка (holder, ISIN) — иначе стари редове се събират многократно
        // (потвърдено на живо: FORVIA излизаше 323% заради 4 стари реда на един и същ holder).
        var endDateIndex = FindColumnIndex(headers, "Date de fin de publication position");

        if (holderIndex < 0 || isinIndex < 0 || percentIndex < 0 || dateIndex < 0 || endDateIndex < 0)
        {
            return [];
        }

        var latestByHolderIsin = new Dictionary<(string Holder, string Isin), (string IssuerName, DateTime PositionDate, decimal Percent, bool StillOpen)>();

        foreach (var line in lines.Skip(1))
        {
            var fields = SplitCsvLine(line, ';');
            if (fields.Count <= percentIndex || fields.Count <= isinIndex || fields.Count <= endDateIndex || fields.Count <= holderIndex)
            {
                continue;
            }

            var isin = fields[isinIndex].Trim();
            var holder = fields[holderIndex].Trim();
            if (string.IsNullOrWhiteSpace(isin) || string.IsNullOrWhiteSpace(holder))
            {
                continue;
            }

            if (!DateTime.TryParse(fields[dateIndex], CultureInfo.InvariantCulture, DateTimeStyles.None, out var positionDate))
            {
                continue;
            }

            var key = (holder, isin);
            if (latestByHolderIsin.TryGetValue(key, out var existing) && existing.PositionDate >= positionDate)
            {
                continue;
            }

            var issuerName = issuerIndex >= 0 && issuerIndex < fields.Count ? fields[issuerIndex].Trim() : isin;
            var percent = ParseDecimal(fields[percentIndex]);
            var stillOpen = string.IsNullOrWhiteSpace(fields[endDateIndex]);

            latestByHolderIsin[key] = (issuerName, positionDate, percent, stillOpen);
        }

        var byIsin = new Dictionary<string, (string IssuerName, DateTime PositionDate, decimal Percent)>();

        foreach (var ((_, isin), entry) in latestByHolderIsin.Where(kvp => kvp.Value.StillOpen))
        {
            if (byIsin.TryGetValue(isin, out var existing))
            {
                byIsin[isin] = (existing.IssuerName, entry.PositionDate > existing.PositionDate ? entry.PositionDate : existing.PositionDate, existing.Percent + entry.Percent);
            }
            else
            {
                byIsin[isin] = (entry.IssuerName, entry.PositionDate, entry.Percent);
            }
        }

        return byIsin.Select(kvp => new AmfShortPosition(kvp.Value.IssuerName, kvp.Key, kvp.Value.PositionDate, kvp.Value.Percent)).ToList();
    }

    private static int FindColumnIndex(List<string> headers, params string[] candidates) =>
        headers.FindIndex(h => candidates.Any(c => string.Equals(h.Trim(), c, StringComparison.OrdinalIgnoreCase)));

    private static List<string> SplitCsvLine(string line, char delimiter = ',')
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == delimiter && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private sealed record AmfShortPosition(string IssuerName, string Isin, DateTime PositionDate, decimal NetShortPositionPercent);

    private const string SecEdgar13DGSource = "SEC-EDGAR-13DG";
    private const string MajorAcquisitionSignalType = "MajorAcquisition";
    private const string SecEdgar13DFeedUrl = "https://www.sec.gov/cgi-bin/browse-edgar?action=getcurrent&type=SC+13D&dateb=&owner=include&count=40&search_text=&output=atom";
    private const string SecEdgar13GFeedUrl = "https://www.sec.gov/cgi-bin/browse-edgar?action=getcurrent&type=SC+13G&dateb=&owner=include&count=40&search_text=&output=atom";

    public async Task<int> CollectSecEdgar13DGSignalsAsync(CancellationToken cancellationToken = default)
    {
        var runLog = new RunLog
        {
            StartedAt = DateTime.UtcNow,
            Status = "Running",
            JobName = SecEdgar13DGSource
        };
        _dbContext.RunLogs.Add(runLog);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var collected = 0;
        try
        {
            var refs = new List<SecEdgar13DGFilingRef>();
            refs.AddRange(await FetchSecEdgar13DGFilingRefsAsync(SecEdgar13DFeedUrl, cancellationToken));
            refs.AddRange(await FetchSecEdgar13DGFilingRefsAsync(SecEdgar13GFeedUrl, cancellationToken));

            var existingLinks = await _dbContext.Signals
                .Where(s => s.Source == SecEdgar13DGSource)
                .Select(s => s.SourceUrl)
                .ToHashSetAsync(cancellationToken);

            foreach (var filingRef in refs)
            {
                // Submission-то е достъпно и като plain-text ".txt" на същия път, замествайки "-index.htm" —
                // това ни дава наведнъж и structured SEC-HEADER-а (issuer/filer имена, дата), и вградения
                // cover-page текст (за процента), с едно единствено HTTP извикване вместо две-три.
                var txtUrl = filingRef.IndexUrl.Replace("-index.htm", ".txt", StringComparison.OrdinalIgnoreCase);
                if (existingLinks.Contains(txtUrl))
                {
                    continue;
                }

                existingLinks.Add(txtUrl);

                SecEdgar13DGFiling? filing;
                try
                {
                    filing = await FetchSecEdgar13DGFilingAsync(txtUrl, filingRef.FormType, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse SEC 13D/13G filing {TxtUrl}", txtUrl);
                    continue;
                }

                if (filing is null)
                {
                    continue;
                }

                var percentText = filing.PercentAcquired.HasValue ? $"{filing.PercentAcquired.Value:0.0##}%" : "n/a";

                _dbContext.Signals.Add(new Signal
                {
                    Source = SecEdgar13DGSource,
                    SignalType = MajorAcquisitionSignalType,
                    SourceUrl = txtUrl,
                    Title = $"{filing.FilerName} acquired {percentText} of {filing.IssuerName} ({filing.FormType})",
                    RawContent = $"Issuer: {filing.IssuerName}; Filer: {filing.FilerName}; Percent acquired: {percentText}; Form: {filing.FormType}; Filed: {filing.FiledDate:yyyy-MM-dd}",
                    PublishedAt = filing.FiledDate,
                    CollectedAt = DateTime.UtcNow,
                    Processed = false,
                    RunLogId = runLog.Id
                });

                collected++;
            }

            runLog.Status = "Completed";
            runLog.SignalsCollected = collected;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect SEC EDGAR 13D/13G signals");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        return collected;
    }

    private async Task<List<SecEdgar13DGFilingRef>> FetchSecEdgar13DGFilingRefsAsync(string feedUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", SecEdgarUserAgent);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

        XNamespace atom = "http://www.w3.org/2005/Atom";
        var filings = new List<SecEdgar13DGFilingRef>();
        var seenAccessionNumbers = new HashSet<string>();

        foreach (var entry in document.Descendants(atom + "entry"))
        {
            var link = entry.Element(atom + "link")?.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(link))
            {
                continue;
            }

            // Всяко подаване се появява веднъж под CIK-а на issuer-а и веднъж под CIK-а на филиращото лице —
            // дедупликираме по accession number, не по URL (виж аналогичния коментар при SEC EDGAR Form 4).
            var accessionMatch = Regex.Match(link, @"(?<accession>\d{10}-\d{2}-\d{6})-index\.htm", RegexOptions.IgnoreCase);
            var dedupKey = accessionMatch.Success ? accessionMatch.Groups["accession"].Value : link;
            if (!seenAccessionNumbers.Add(dedupKey))
            {
                continue;
            }

            var formType = entry.Element(atom + "category")?.Attribute("term")?.Value ?? "SC 13D";
            filings.Add(new SecEdgar13DGFilingRef(link, formType));
        }

        return filings;
    }

    private async Task<SecEdgar13DGFiling?> FetchSecEdgar13DGFilingAsync(string txtUrl, string formType, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, txtUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", SecEdgarUserAgent);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        var issuerName = ExtractHeaderName(raw, "SUBJECT COMPANY:");
        var filerName = ExtractHeaderName(raw, "FILED BY:");
        if (string.IsNullOrWhiteSpace(issuerName) || string.IsNullOrWhiteSpace(filerName))
        {
            return null;
        }

        var filedDateMatch = Regex.Match(raw, @"FILED AS OF DATE:\s*(?<date>\d{8})");
        var filedDate = filedDateMatch.Success &&
            DateTime.TryParseExact(filedDateMatch.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedFiledDate)
            ? parsedFiledDate
            : DateTime.UtcNow.Date;

        // Процентът е само в cover page-а на самия документ (не в SEC-HEADER-а), под стандартната
        // точка 13 "PERCENT OF CLASS REPRESENTED BY AMOUNT IN ROW (11)" — потвърдено на живо срещу
        // реално подаване. Свалят се HTML таговете, за да работи регексът еднакво за .htm и .txt съдържание.
        var plainText = Regex.Replace(raw, "<[^>]+>", " ");
        var percentMatch = Regex.Match(
            plainText,
            @"PERCENT OF CLASS REPRESENTED BY AMOUNT IN ROW \(11\)\s*(?<percent>\d{1,3}(?:\.\d+)?)",
            RegexOptions.IgnoreCase);
        var percent = percentMatch.Success &&
            decimal.TryParse(percentMatch.Groups["percent"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedPercent)
            ? parsedPercent
            : (decimal?)null;

        return new SecEdgar13DGFiling(issuerName, filerName, percent, filedDate, formType);
    }

    // SEC-HEADER-ът структурира filer/issuer имена като "<Label>:\n\n\tCOMPANY DATA:\n\t\tCOMPANY CONFORMED NAME:\t\tXxx"
    // (или "OWNER DATA" вместо "COMPANY DATA", когато филиращото лице е физическо, не фирма) — вземаме
    // първото "CONFORMED NAME:" след label-а, независимо кой от двата под-блока се появи.
    private static string? ExtractHeaderName(string raw, string label)
    {
        var labelIndex = raw.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (labelIndex < 0)
        {
            return null;
        }

        var nameMatch = Regex.Match(raw[labelIndex..], @"CONFORMED NAME:\s*(?<name>[^\r\n]+)", RegexOptions.IgnoreCase);
        return nameMatch.Success ? nameMatch.Groups["name"].Value.Trim() : null;
    }

    private sealed record SecEdgar13DGFilingRef(string IndexUrl, string FormType);

    private sealed record SecEdgar13DGFiling(string IssuerName, string FilerName, decimal? PercentAcquired, DateTime FiledDate, string FormType);

    private const string EpParliamentSource = "EP-Parliament";
    private const string PreLegislativeSignalType = "PreLegislative";
    // EP Open Data API v2 — свободен достъп, без ключ, лимит 500 заявки/5мин (виж openapi спецификацията на
    // https://data.europarl.europa.eu/api/v2/). Този feed връща draft committee reports/opinions, публикувани
    // или обновени през последния месец — комисиите изготвят тези документи МЕСЕЦИ преди финалното приемане и
    // публикуване в EUR-Lex Official Journal, затова е най-ранният сигнал в системата за предстоящо законодателство.
    private const string EpCommitteeFeedUrl = "https://data.europarl.europa.eu/api/v2/committee-documents/feed";
    // User-Agent-ът е формално по избор в EP-ската OpenAPI спецификация, но заявка без него връща 403 —
    // потвърдено на живо (2026-08-05). Форматът следва препоръчания в спецификацията "{user-id}-{env}-{version}".
    private const string EpParliamentUserAgent = "EarlySignalSystem-dev-1.0.0";

    public async Task<int> CollectEpParliamentSignalsAsync(CancellationToken cancellationToken = default)
    {
        var runLog = new RunLog
        {
            StartedAt = DateTime.UtcNow,
            Status = "Running",
            JobName = EpParliamentSource
        };
        _dbContext.RunLogs.Add(runLog);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var collected = 0;
        try
        {
            var items = await FetchEpCommitteeFeedItemsAsync(cancellationToken);

            var existingLinks = await _dbContext.Signals
                .Where(s => s.Source == EpParliamentSource)
                .Select(s => s.SourceUrl)
                .ToHashSetAsync(cancellationToken);

            foreach (var item in items)
            {
                if (existingLinks.Contains(item.Link))
                {
                    continue;
                }

                _dbContext.Signals.Add(new Signal
                {
                    Source = EpParliamentSource,
                    SignalType = PreLegislativeSignalType,
                    SourceUrl = item.Link,
                    Title = string.IsNullOrWhiteSpace(item.CommitteeCode) ? item.Title : $"[{item.CommitteeCode}] {item.Title}",
                    RawContent = item.DocumentTypeLabel,
                    PublishedAt = item.UpdatedAt,
                    CollectedAt = DateTime.UtcNow,
                    Processed = false,
                    RunLogId = runLog.Id
                });

                existingLinks.Add(item.Link);
                collected++;
            }

            runLog.Status = "Completed";
            runLog.SignalsCollected = collected;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect EP Parliament signals");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        return collected;
    }

    private async Task<List<EpCommitteeFeedItem>> FetchEpCommitteeFeedItemsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, EpCommitteeFeedUrl);
        request.Headers.Accept.ParseAdd("application/atom+xml");
        request.Headers.TryAddWithoutValidation("User-Agent", EpParliamentUserAgent);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

        XNamespace atom = "http://www.w3.org/2005/Atom";
        var items = new List<EpCommitteeFeedItem>();

        foreach (var entry in document.Descendants(atom + "entry"))
        {
            var title = entry.Element(atom + "title")?.Value.Trim();
            // <id> е постоянен, dereferenceable URI към документа (напр. .../eli/dl/doc/ECON-PR-778137) —
            // ползваме го и като SourceUrl за dedup, и като линк, вместо rel="alternate" (сочи към суровия API endpoint).
            var link = entry.Element(atom + "id")?.Value.Trim();

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
            {
                continue;
            }

            var documentTypeLabel = entry.Element(atom + "category")?.Attribute("label")?.Value;

            var updatedRaw = entry.Element(atom + "updated")?.Value;
            var updatedAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(updatedRaw) &&
                DateTimeOffset.TryParse(updatedRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                updatedAt = parsed.UtcDateTime;
            }

            // Документ ID-тата следват "<COMMITTEE_CODE>-<TYPE>-<NUMBER>" формат (напр. "ECON-PR-778137") —
            // комитетският код е полезен контекст в Title-а без да викаме отделен endpoint за пълните имена.
            var identifier = link[(link.LastIndexOf('/') + 1)..];
            var committeeCode = identifier.Contains('-') ? identifier[..identifier.IndexOf('-')] : string.Empty;

            items.Add(new EpCommitteeFeedItem(title, link, documentTypeLabel, committeeCode, updatedAt));
        }

        return items;
    }

    private sealed record EpCommitteeFeedItem(string Title, string Link, string? DocumentTypeLabel, string CommitteeCode, DateTime UpdatedAt);
}
