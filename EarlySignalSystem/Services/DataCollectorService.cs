using System.Globalization;
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
}
