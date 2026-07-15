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
    private const string BudgetChangeSignalType = "BudgetChange";
    // OECD.SDD.NAD, "Annual government deficit/surplus, revenue, expenditure and main aggregates IDC" (DF_TABLE12_IDC),
    // filtered към FREQ=A, REF_SECTOR=S13 (General government), ACCOUNTING_ENTRY=B (Balancing item), STO=B9
    // (Net lending/net borrowing) — истинско, проверено на живо government-deficit измерение. Няма quarterly
    // еквивалент с sector breakdown в OECD SDMX (проверено): quarterly national accounts покриват само total economy.
    private const string OecdApiUrl = "https://sdmx.oecd.org/public/rest/data/OECD.SDD.NAD,DSD_NASEC10_IDC@DF_TABLE12_IDC,1.0/A....S13...B.B9..........";
    private const decimal SignificantChangePercent = 5m;

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
            // Временно деактивирано: годишните OECD government-deficit данни се оказаха неподходящи за
            // real-time signal detection (year-over-year сравнение на исторически годишни стойности създава
            // шум, не действителни "нови" сигнали). Fetch/parse логиката отдолу (FetchOecdBudgetChangesAsync,
            // ParseOecdSdmxJson, BuildOecdSeriesLabel) е запазена непокътната за бъдещо re-enable с по-подходящ
            // dataset. Хвърляме веднага, за да падне в catch-а по-долу без да се пипа останалата логика.
            throw new InvalidOperationException("OECD collector temporarily disabled - historical data not suitable for signal detection");
#pragma warning disable CS0162 // Unreachable code detected — запазено непокътнато за бъдещо re-enable, виж коментара по-горе.

            var changes = await FetchOecdBudgetChangesAsync(cancellationToken);

            var existingLinks = await _dbContext.Signals
                .Where(s => s.Source == OecdSource)
                .Select(s => s.SourceUrl)
                .ToHashSetAsync(cancellationToken);

            foreach (var change in changes)
            {
                if (Math.Abs(change.PercentChange) < SignificantChangePercent || existingLinks.Contains(change.SourceUrl))
                {
                    continue;
                }

                _dbContext.Signals.Add(new Signal
                {
                    Source = OecdSource,
                    SignalType = BudgetChangeSignalType,
                    SourceUrl = change.SourceUrl,
                    Title = $"{change.SeriesLabel} general government balance: {change.PercentChange:+0.0;-0.0}% year-over-year",
                    RawContent = $"Previous: {change.PreviousValue}; Latest: {change.LatestValue}; Period: {change.Period}",
                    PublishedAt = DateTime.UtcNow,
                    CollectedAt = DateTime.UtcNow,
                    Processed = false,
                    RunLogId = runLog.Id
                });

                existingLinks.Add(change.SourceUrl);
                collected++;
            }

            runLog.Status = "Completed";
            runLog.SignalsCollected = collected;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OECD collector disabled");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return 0;
        }

        return collected;
#pragma warning restore CS0162
    }

    private async Task<List<OecdBudgetChange>> FetchOecdBudgetChangesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OecdApiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.sdmx.data+json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ParseOecdSdmxJson(payload, OecdApiUrl);
    }

    private static List<OecdBudgetChange> ParseOecdSdmxJson(JsonDocument payload, string sourceUrl)
    {
        var results = new List<OecdBudgetChange>();

        // sdmx.oecd.org връща SDMX-JSON 2.0.0: реалният payload е под "data" (dataSets/structures), не directly на root.
        var root = payload.RootElement.GetProperty("data");
        var dataSets = root.GetProperty("dataSets");
        if (dataSets.GetArrayLength() == 0)
        {
            return results;
        }

        // "structures" е масив (не единичен "structure" обект както в по-старата версия на схемата).
        var dimensions = root.GetProperty("structures")[0].GetProperty("dimensions");
        var seriesDimensions = dimensions.GetProperty("series");
        var observationPeriods = dimensions.GetProperty("observation")[0].GetProperty("values")
            .EnumerateArray()
            .Select(v => v.GetProperty("id").GetString() ?? string.Empty)
            .ToList();

        foreach (var series in dataSets[0].GetProperty("series").EnumerateObject())
        {
            var observations = series.Value.GetProperty("observations").EnumerateObject()
                .Where(o => o.Value[0].ValueKind == JsonValueKind.Number)
                .Select(o => (Index: int.Parse(o.Name, CultureInfo.InvariantCulture), Value: o.Value[0].GetDecimal()))
                .OrderBy(o => o.Index)
                .ToList();

            if (observations.Count < 2)
            {
                continue;
            }

            var previous = observations[^2];
            var latest = observations[^1];
            if (previous.Value == 0)
            {
                continue;
            }

            var percentChange = (latest.Value - previous.Value) / Math.Abs(previous.Value) * 100m;
            var period = latest.Index < observationPeriods.Count ? observationPeriods[latest.Index] : latest.Index.ToString(CultureInfo.InvariantCulture);

            results.Add(new OecdBudgetChange(
                BuildOecdSeriesLabel(series.Name, seriesDimensions),
                previous.Value,
                latest.Value,
                percentChange,
                period,
                $"{sourceUrl}#{series.Name}:{period}"));
        }

        return results;
    }

    // Заявката е закачена за един конкретен индикатор (general government net lending/borrowing) — единствената
    // променлива координата по série е REF_AREA, затова label-ът е просто името на държавата, не dump на
    // всичките 19 dimension-а от DSD (повечето от които "Not applicable" за тази заявка).
    private static string BuildOecdSeriesLabel(string seriesKey, JsonElement seriesDimensions)
    {
        var indices = seriesKey.Split(':');

        for (var i = 0; i < seriesDimensions.GetArrayLength(); i++)
        {
            var dimension = seriesDimensions[i];
            if (dimension.GetProperty("id").GetString() != "REF_AREA")
            {
                continue;
            }

            if (i >= indices.Length || !int.TryParse(indices[i], out var valueIndex))
            {
                break;
            }

            var values = dimension.GetProperty("values");
            if (valueIndex >= 0 && valueIndex < values.GetArrayLength())
            {
                return values[valueIndex].GetProperty("name").GetString() ?? seriesKey;
            }

            break;
        }

        return seriesKey;
    }

    private sealed record OecdBudgetChange(string SeriesLabel, decimal PreviousValue, decimal LatestValue, decimal PercentChange, string Period, string SourceUrl);

    private const string EsmaSource = "ESMA";
    private const string ShortInterestDeclineSignalType = "ShortInterestDecline";
    private const string EsmaCsvUrl = "https://www.esma.europa.eu/sites/default/files/library/ShortPositions.csv";
    private const decimal ShortInterestDeclineThresholdPoints = 1m;
    private const string EsmaSnapshotPrefix = "ESMA_SNAPSHOT:";

    public async Task<int> CollectEsmaSignalsAsync(CancellationToken cancellationToken = default)
    {
        var runLog = new RunLog
        {
            StartedAt = DateTime.UtcNow,
            Status = "Running",
            JobName = EsmaSource
        };
        _dbContext.RunLogs.Add(runLog);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var collected = 0;
        try
        {
            var positions = await FetchEsmaShortPositionsAsync(cancellationToken);

            // ESMA публикува само текущ snapshot (не времеви редове), затова пазим предходния snapshot в RunLog.Notes,
            // за да можем да сравняваме спад спрямо предходния run — само декларираните спадове стават Signal записи.
            var previousRunLog = await _dbContext.RunLogs
                .Where(r => r.Notes != null && r.Notes.StartsWith(EsmaSnapshotPrefix))
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var previousPercents = previousRunLog is not null
                ? JsonSerializer.Deserialize<Dictionary<string, decimal>>(previousRunLog.Notes![EsmaSnapshotPrefix.Length..]) ?? new Dictionary<string, decimal>()
                : new Dictionary<string, decimal>();

            var existingLinks = await _dbContext.Signals
                .Where(s => s.Source == EsmaSource)
                .Select(s => s.SourceUrl)
                .ToHashSetAsync(cancellationToken);

            foreach (var position in positions)
            {
                if (!previousPercents.TryGetValue(position.Isin, out var previousPercent))
                {
                    continue;
                }

                var decline = previousPercent - position.NetShortPositionPercent;
                var sourceUrl = $"{EsmaCsvUrl}#{position.Isin}:{position.PositionDate:yyyy-MM-dd}";
                if (decline <= ShortInterestDeclineThresholdPoints || existingLinks.Contains(sourceUrl))
                {
                    continue;
                }

                _dbContext.Signals.Add(new Signal
                {
                    Source = EsmaSource,
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
            runLog.Notes = EsmaSnapshotPrefix + JsonSerializer.Serialize(positions.ToDictionary(p => p.Isin, p => p.NetShortPositionPercent));
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // ESMA премести net-short-position данните към CAPTCHA-защитен registers портал —
            // автоматичен достъп вече не е възможен без официален API ключ. Логваме и продължаваме
            // (не хвърляме), за да не блокираме останалите collector-и/Hangfire retry-и.
            // Fetch/parse логиката по-долу е запазена за когато се появи алтернативен endpoint.
            _logger.LogWarning(ex, "ESMA endpoint unavailable");
            runLog.Status = "Failed";
            runLog.ErrorMessage = "ESMA endpoint unavailable";
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return 0;
        }

        return collected;
    }

    private async Task<List<EsmaShortPosition>> FetchEsmaShortPositionsAsync(CancellationToken cancellationToken)
    {
        var csv = await _httpClient.GetStringAsync(EsmaCsvUrl, cancellationToken);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return [];
        }

        var headers = SplitCsvLine(lines[0]);
        var issuerIndex = FindColumnIndex(headers, "issuer", "name of the issuer", "issuer name");
        var isinIndex = FindColumnIndex(headers, "isin", "isin code");
        var dateIndex = FindColumnIndex(headers, "position date", "date");
        var percentIndex = FindColumnIndex(headers, "net short position", "net short position (%)", "position (%)");

        if (isinIndex < 0 || percentIndex < 0)
        {
            return [];
        }

        var byIsin = new Dictionary<string, (string IssuerName, DateTime PositionDate, decimal Percent)>();

        foreach (var line in lines.Skip(1))
        {
            var fields = SplitCsvLine(line);
            if (fields.Count <= percentIndex || fields.Count <= isinIndex)
            {
                continue;
            }

            var isin = fields[isinIndex].Trim();
            if (string.IsNullOrWhiteSpace(isin))
            {
                continue;
            }

            var issuerName = issuerIndex >= 0 && issuerIndex < fields.Count ? fields[issuerIndex].Trim() : isin;
            var percent = ParseDecimal(fields[percentIndex]);
            var positionDate = dateIndex >= 0 && dateIndex < fields.Count &&
                DateTime.TryParse(fields[dateIndex], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                ? parsedDate
                : DateTime.UtcNow.Date;

            if (byIsin.TryGetValue(isin, out var existing))
            {
                byIsin[isin] = (existing.IssuerName, positionDate > existing.PositionDate ? positionDate : existing.PositionDate, existing.Percent + percent);
            }
            else
            {
                byIsin[isin] = (issuerName, positionDate, percent);
            }
        }

        return byIsin.Select(kvp => new EsmaShortPosition(kvp.Value.IssuerName, kvp.Key, kvp.Value.PositionDate, kvp.Value.Percent)).ToList();
    }

    private static int FindColumnIndex(List<string> headers, params string[] candidates) =>
        headers.FindIndex(h => candidates.Any(c => string.Equals(h.Trim(), c, StringComparison.OrdinalIgnoreCase)));

    private static List<string> SplitCsvLine(string line)
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
            else if (c == ',' && !inQuotes)
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

    private sealed record EsmaShortPosition(string IssuerName, string Isin, DateTime PositionDate, decimal NetShortPositionPercent);

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
}
