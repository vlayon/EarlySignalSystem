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
    // По-строгият prompt изисква 2-изреченски structured rationale за всяка компания — 1000 токена бяха
    // прекалено малко и Claude режеше отговора си по средата на JSON-а (truncated -> невалиден JSON, batch-ът
    // оставаше Processed=false завинаги, потвърдено на живо).
    private const int MaxTokens = 2048;
    private const int BatchSize = 15;
    private const string JobName = "AI-Analyzer";

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

            // Един RunLog на batch, за да могат SectorScores/CompanyPicks да се свържат обратно към
            // сигналите, от които произлизат (виж CumulativeScoringService — Signal Diversity минава през RunLogId).
            var runLog = new RunLog
            {
                StartedAt = DateTime.UtcNow,
                Status = "Running",
                JobName = JobName
            };
            _dbContext.RunLogs.Add(runLog);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var analyzedAt = DateTime.UtcNow;

            // Companies със същото име могат да се появят повторно в един и същ AI batch — кешираме
            // резолвнатите/новосъздадените Company записи по име, за да не удряме DB за всеки company pick
            // и да не се опитаме да вкараме дублиран ред (нарушение на unique index-а по CompanyName).
            var companyCache = new Dictionary<string, Company>(StringComparer.OrdinalIgnoreCase);

            foreach (var sector in analysis.Sectors)
            {
                _dbContext.SectorScores.Add(new SectorScore
                {
                    SectorName = sector.Sector,
                    Score = sector.Score,
                    Rationale = string.IsNullOrWhiteSpace(sector.Trend)
                        ? sector.Rationale
                        : $"[{sector.Trend}] {sector.Rationale}",
                    ScoredAt = analyzedAt,
                    RunLogId = runLog.Id
                });
            }

            foreach (var company in analysis.Companies)
            {
                var resolvedCompany = await ResolveCompanyAsync(company.CompanyName, companyCache, cancellationToken);

                var companyPick = new CompanyPick
                {
                    Ticker = resolvedCompany.Ticker,
                    CompanyName = company.CompanyName,
                    Sector = company.Sector,
                    ConfidenceScore = company.Score,
                    Rationale = company.Rationale,
                    Sentiment = NormalizeSentiment(company.Sentiment),
                    PickedAt = analyzedAt,
                    RunLogId = runLog.Id
                };

                // signalCelexIds сочи към signal.Id-тата (посочени в prompt-а като [ID:x]) на сигналите от
                // ТОЗИ batch, от които AI-ят е извел pick-а — свързваме ги през CompanyPickSignals, за да може
                // Shortlist.razor да покаже точно тези сигнали, не целия batch.
                foreach (var signalRef in company.SignalCelexIds ?? [])
                {
                    var idText = signalRef.Replace("CELEX:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                    if (!int.TryParse(idText, out var signalId))
                    {
                        continue;
                    }

                    var matchedSignal = batch.FirstOrDefault(s => s.Id == signalId);
                    if (matchedSignal is not null)
                    {
                        companyPick.CompanyPickSignals.Add(new CompanyPickSignal { Signal = matchedSignal });
                    }
                }

                _dbContext.CompanyPicks.Add(companyPick);
            }

            foreach (var signal in batch)
            {
                signal.Processed = true;
                signal.RunLogId = runLog.Id;
            }

            runLog.Status = "Completed";
            runLog.SignalsCollected = batch.Length;
            runLog.CompletedAt = DateTime.UtcNow;

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

    // Търси Company по partial, case-insensitive match на името (AI-ят и Companies таблицата рядко се
    // договарят за идентична форма на името, напр. "BMW" срещу "Bayerische Motoren Werke AG").
    // Ако не намери нищо, създава нов Company запис с Ticker = null — ще се резолвне по-късно от
    // TickerVerificationService.
    private async Task<Company> ResolveCompanyAsync(string companyName, Dictionary<string, Company> companyCache, CancellationToken cancellationToken)
    {
        if (companyCache.TryGetValue(companyName, out var cached))
        {
            return cached;
        }

        var existing = await _dbContext.Companies
            .FirstOrDefaultAsync(c => c.CompanyName.Contains(companyName) || companyName.Contains(c.CompanyName), cancellationToken);

        if (existing is not null)
        {
            companyCache[companyName] = existing;
            return existing;
        }

        var newCompany = new Company
        {
            CompanyName = companyName,
            TickerVerified = false,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _dbContext.Companies.Add(newCompany);
        await _dbContext.SaveChangesAsync(cancellationToken);

        companyCache[companyName] = newCompany;
        return newCompany;
    }

    private static string BuildPrompt(Signal[] batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analyze these regulatory/legislative signals and identify companies with DIRECT and SPECIFIC business impact.");
        sb.AppendLine("Only include a company if: (1) the regulation directly targets its industry/product category, (2) the company has a concrete competitive advantage from this specific regulation, (3) you can explain in 2 sentences exactly HOW the regulation affects THIS company's revenue or market position.");
        sb.AppendLine("DO NOT include companies based on: general sector exposure, country of domicile, or indirect/speculative connections.");
        sb.AppendLine("Return ONLY real, publicly listed companies that trade on a major stock exchange (NYSE, NASDAQ, London Stock Exchange, Euronext, XETRA/Frankfurt, Borsa Italiana, Madrid Stock Exchange).");
        sb.AppendLine("Do NOT return: industry categories, government agencies, subsidiaries of private companies, consortiums, or any entity without a publicly traded stock.");
        sb.AppendLine("Do NOT return descriptions like \"X or similar companies\" or \"companies that provide Y\".");
        sb.AppendLine("If you cannot identify a specific publicly listed company directly affected, skip it entirely — return fewer companies rather than inventing ones.");
        sb.AppendLine("Maximum 5 companies per batch, only if you are certain they are publicly listed.");
        sb.AppendLine("For each company, the rationale MUST follow this format: \"[Specific regulation/signal] directly affects [company] because [specific business reason]. Unlike competitors, [company] is better positioned because [concrete differentiator].\"");
        sb.AppendLine("Each signal below is prefixed with its reference ID, e.g. \"[ID:123]\". For each company, \"signalCelexIds\" MUST list the exact reference ID(s) (as strings) of the signal(s) below that this pick is based on.");
        sb.AppendLine("Each company MUST also include a \"sentiment\" field, one of exactly: \"Bullish\" (the company is expected to benefit from the signal), \"Bearish\" (expected to lose or be threatened by it), or \"Neutral\" (affected, but the direction is unclear). This field is required, never omit it.");
        sb.AppendLine("Do NOT include a ticker symbol — company tickers are resolved separately after analysis.");
        sb.AppendLine("Return strict JSON only, no preamble, matching this exact structure:");
        sb.AppendLine("{ \"sectors\": [ { \"sector\": \"string\", \"score\": 0-100, \"trend\": \"Rising|Stable|Falling\", \"rationale\": \"string\" } ], \"companies\": [ { \"companyName\": \"string\", \"sector\": \"string\", \"score\": 0-100, \"rationale\": \"string\", \"sentiment\": \"Bullish|Bearish|Neutral\", \"signalCelexIds\": [\"string\"] } ] }");
        sb.AppendLine();
        sb.AppendLine("Signals:");
        foreach (var signal in batch)
        {
            sb.AppendLine($"- [ID:{signal.Id}] [{signal.Source}] {signal.Title} (published {signal.PublishedAt:yyyy-MM-dd})");
            if (!string.IsNullOrWhiteSpace(signal.RawContent))
            {
                sb.AppendLine($"  {signal.RawContent}");
            }
        }

        return sb.ToString();
    }

    private static string? NormalizeSentiment(string? sentiment) => sentiment?.Trim() switch
    {
        "Bullish" => "Bullish",
        "Bearish" => "Bearish",
        "Neutral" => "Neutral",
        _ => "Neutral"
    };

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
        public string CompanyName { get; set; } = string.Empty;
        public string Sector { get; set; } = string.Empty;
        public decimal Score { get; set; }
        public string? Rationale { get; set; }
        public string? Sentiment { get; set; }
        public List<string>? SignalCelexIds { get; set; }
    }
}
