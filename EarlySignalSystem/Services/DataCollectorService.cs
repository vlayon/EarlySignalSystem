using System.Globalization;
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
            Status = "Running"
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
            Status = "Running"
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
                    Title = $"{purchase.ReportingOwnerName} bought {purchase.TotalShares:N0} shares of {purchase.IssuerName}",
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

        return new SecEdgarPurchase(issuerName, ticker, ownerName, totalShares, totalCost / totalShares, transactionDate);
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

    private sealed record SecEdgarPurchase(string IssuerName, string? Ticker, string ReportingOwnerName, decimal TotalShares, decimal WeightedAveragePrice, DateTime TransactionDate);

    private const string TedSource = "TED";
    private const string GovernmentContractSignalType = "GovernmentContract";
    private const string TedFeedUrl = "https://ted.europa.eu/TED/misc/rssCurrentNotices.do";
    private const decimal MinContractValueEur = 1_000_000m;

    public async Task<int> CollectTedSignalsAsync(CancellationToken cancellationToken = default)
    {
        var runLog = new RunLog
        {
            StartedAt = DateTime.UtcNow,
            Status = "Running"
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
                if (existingLinks.Contains(notice.Link) || notice.ValueEur is null || notice.ValueEur < MinContractValueEur)
                {
                    continue;
                }

                _dbContext.Signals.Add(new Signal
                {
                    Source = TedSource,
                    SignalType = GovernmentContractSignalType,
                    SourceUrl = notice.Link,
                    Title = notice.Title,
                    RawContent = $"Value: {notice.ValueEur:N0} EUR; Buyer: {notice.Buyer ?? "n/a"}; Winner: {notice.Winner ?? "n/a"}",
                    PublishedAt = notice.PublishedAt,
                    CollectedAt = DateTime.UtcNow,
                    Processed = false,
                    RunLogId = runLog.Id
                });

                existingLinks.Add(notice.Link);
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
        await using var stream = await _httpClient.GetStreamAsync(TedFeedUrl, cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

        var notices = new List<TedNotice>();
        foreach (var item in document.Descendants("item"))
        {
            var title = item.Element("title")?.Value.Trim() ?? string.Empty;
            var link = item.Element("link")?.Value.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
            {
                continue;
            }

            var description = item.Element("description")?.Value ?? string.Empty;
            var pubDateRaw = item.Element("pubDate")?.Value;
            var publishedAt = DateTimeOffset.TryParse(pubDateRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed.UtcDateTime
                : DateTime.UtcNow;

            notices.Add(new TedNotice(
                title,
                link,
                publishedAt,
                ExtractTedValueEur(description),
                ExtractTedField(description, "Contracting authority", "Buyer"),
                ExtractTedField(description, "Contract award to", "Winner")));
        }

        return notices;
    }

    private static decimal? ExtractTedValueEur(string description)
    {
        var match = Regex.Match(description, @"(?<amount>[\d.,]{4,})\s*EUR", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var normalized = match.Groups["amount"].Value.Replace(".", string.Empty).Replace(",", string.Empty);
        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string? ExtractTedField(string description, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = Regex.Match(description, $@"{Regex.Escape(label)}\s*:\s*(?<value>[^<\n]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups["value"].Value.Trim();
            }
        }

        return null;
    }

    private sealed record TedNotice(string Title, string Link, DateTime PublishedAt, decimal? ValueEur, string? Buyer, string? Winner);

    private const string OecdSource = "OECD";
    private const string BudgetChangeSignalType = "BudgetChange";
    private const string OecdApiUrl = "https://sdmx.oecd.org/public/rest/data/OECD.GOV.GFC,DSD_GFSQ@DF_GFSQ/";
    private const string OecdFallbackApiUrl = "https://stats.oecd.org/SDMX-JSON/data/GOV_10Q_GGNFA/";
    private const decimal SignificantChangePercent = 5m;

    public async Task<int> CollectOecdSignalsAsync(CancellationToken cancellationToken = default)
    {
        var runLog = new RunLog
        {
            StartedAt = DateTime.UtcNow,
            Status = "Running"
        };
        _dbContext.RunLogs.Add(runLog);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var collected = 0;
        try
        {
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
                    Title = $"{change.SeriesLabel}: {change.PercentChange:+0.0;-0.0}% quarter-over-quarter",
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
            _logger.LogError(ex, "Failed to collect OECD signals");
            runLog.Status = "Failed";
            runLog.ErrorMessage = ex.Message;
            runLog.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        return collected;
    }

    private async Task<List<OecdBudgetChange>> FetchOecdBudgetChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await FetchOecdSdmxJsonAsync(OecdApiUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Основният SDMX 3.0 endpoint понякога връща грешка за конкретни dataset ключове — при провал пробваме legacy stats.oecd.org формата.
            _logger.LogWarning(ex, "OECD primary endpoint failed, falling back to legacy SDMX-JSON endpoint");
            return await FetchOecdSdmxJsonAsync(OecdFallbackApiUrl, cancellationToken);
        }
    }

    private async Task<List<OecdBudgetChange>> FetchOecdSdmxJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.sdmx.data+json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ParseOecdSdmxJson(payload, url);
    }

    private static List<OecdBudgetChange> ParseOecdSdmxJson(JsonDocument payload, string sourceUrl)
    {
        var results = new List<OecdBudgetChange>();

        var root = payload.RootElement;
        var dataSets = root.GetProperty("dataSets");
        if (dataSets.GetArrayLength() == 0)
        {
            return results;
        }

        var dimensions = root.GetProperty("structure").GetProperty("dimensions");
        var seriesDimensions = dimensions.GetProperty("series");
        var observationPeriods = dimensions.GetProperty("observation")[0].GetProperty("values")
            .EnumerateArray()
            .Select(v => v.GetProperty("id").GetString() ?? string.Empty)
            .ToList();

        foreach (var series in dataSets[0].GetProperty("series").EnumerateObject())
        {
            var observations = series.Value.GetProperty("observations").EnumerateObject()
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

    private static string BuildOecdSeriesLabel(string seriesKey, JsonElement seriesDimensions)
    {
        var indices = seriesKey.Split(':');
        var labels = new List<string>();

        for (var i = 0; i < indices.Length && i < seriesDimensions.GetArrayLength(); i++)
        {
            if (!int.TryParse(indices[i], out var valueIndex))
            {
                continue;
            }

            var values = seriesDimensions[i].GetProperty("values");
            if (valueIndex < 0 || valueIndex >= values.GetArrayLength())
            {
                continue;
            }

            var name = values[valueIndex].GetProperty("name").GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                labels.Add(name);
            }
        }

        return labels.Count > 0 ? string.Join(" - ", labels) : seriesKey;
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
            Status = "Running"
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
}
