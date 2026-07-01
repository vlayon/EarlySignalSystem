using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EarlySignalSystem.Data;
using EarlySignalSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace EarlySignalSystem.Services;

public class AiAnalyzerService : IAiAnalyzerService
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string Model = "claude-haiku-4-5";
    private const int MaxTokens = 1000;
    private const int BatchSize = 15;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AiAnalyzerService> _logger;

    public AiAnalyzerService(AppDbContext dbContext, HttpClient httpClient, IConfiguration configuration, ILogger<AiAnalyzerService> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<int> AnalyzeSignalsAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Anthropic:ApiKey configuration is missing.");
        }

        var unprocessedSignals = await _dbContext.Signals
            .Where(s => !s.Processed)
            .OrderBy(s => s.PublishedAt)
            .ToListAsync(cancellationToken);

        var processedCount = 0;

        foreach (var batch in unprocessedSignals.Chunk(BatchSize))
        {
            var analysis = await AnalyzeBatchAsync(batch, apiKey, cancellationToken);
            if (analysis is null)
            {
                continue;
            }

            var analyzedAt = DateTime.UtcNow;

            foreach (var sector in analysis.Sectors)
            {
                _dbContext.SectorScores.Add(new SectorScore
                {
                    SectorName = sector.Sector,
                    Score = sector.Score,
                    Rationale = string.IsNullOrWhiteSpace(sector.Trend)
                        ? sector.Rationale
                        : $"[{sector.Trend}] {sector.Rationale}",
                    ScoredAt = analyzedAt
                });
            }

            foreach (var company in analysis.Companies)
            {
                _dbContext.CompanyPicks.Add(new CompanyPick
                {
                    Ticker = company.Ticker,
                    CompanyName = company.CompanyName,
                    Sector = company.Sector,
                    ConfidenceScore = company.Score,
                    Rationale = company.Rationale,
                    PickedAt = analyzedAt
                });
            }

            foreach (var signal in batch)
            {
                signal.Processed = true;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            processedCount += batch.Length;
        }

        return processedCount;
    }

    private async Task<ClaudeAnalysisResult?> AnalyzeBatchAsync(Signal[] batch, string apiKey, CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model = Model,
            max_tokens = MaxTokens,
            messages = new[]
            {
                new { role = "user", content = BuildPrompt(batch) }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadFromJsonAsync<ClaudeMessageResponse>(cancellationToken: cancellationToken);
        var text = responseBody?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Claude API returned no text content for a signal batch");
            return null;
        }

        return ParseAnalysis(text);
    }

    private static string BuildPrompt(Signal[] batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an investment analyst. Analyze the following signals and respond with ONLY valid JSON matching this exact structure, no other text:");
        sb.AppendLine("{ \"sectors\": [ { \"sector\": \"string\", \"score\": 0-100, \"trend\": \"Rising|Stable|Falling\", \"rationale\": \"string\" } ], \"companies\": [ { \"ticker\": \"string\", \"companyName\": \"string\", \"sector\": \"string\", \"score\": 0-100, \"rationale\": \"string\" } ] }");
        sb.AppendLine();
        sb.AppendLine("Signals:");
        foreach (var signal in batch)
        {
            sb.AppendLine($"- [{signal.Source}] {signal.Title} (published {signal.PublishedAt:yyyy-MM-dd})");
            if (!string.IsNullOrWhiteSpace(signal.RawContent))
            {
                sb.AppendLine($"  {signal.RawContent}");
            }
        }

        return sb.ToString();
    }

    private ClaudeAnalysisResult? ParseAnalysis(string text)
    {
        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            _logger.LogWarning("Could not locate a JSON object in the Claude response: {Text}", text);
            return null;
        }

        var json = text[jsonStart..(jsonEnd + 1)];
        try
        {
            return JsonSerializer.Deserialize<ClaudeAnalysisResult>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Claude analysis JSON: {Json}", json);
            return null;
        }
    }

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

    private sealed class ClaudeAnalysisResult
    {
        public List<SectorAnalysis> Sectors { get; set; } = new();
        public List<CompanyAnalysis> Companies { get; set; } = new();
    }

    private sealed class SectorAnalysis
    {
        public string Sector { get; set; } = string.Empty;
        public decimal Score { get; set; }
        public string? Trend { get; set; }
        public string? Rationale { get; set; }
    }

    private sealed class CompanyAnalysis
    {
        public string Ticker { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Sector { get; set; } = string.Empty;
        public decimal Score { get; set; }
        public string? Rationale { get; set; }
    }
}
