# Skill: Нов AI Analyzer Prompt към Claude API

Използвай този skill, когато добавяш нов вид анализ през Claude API (нов prompt, нова структура на резултата, или допълнителна логика върху вече събраните `Signal` записи).

## Къде живее кодът

`Services/AiAnalyzerService.cs`, имплементиращ `Services/IAiAnalyzerService.cs`. Един public метод на вид анализ (напр. `AnalyzeSignalsAsync`). Ако добавяш нов вид анализ, добави нов метод в същия клас/интерфейс по същия shape — не създавай отделен service, освен ако логиката стане достатъчно различна и голяма.

## Структура на интерфейса

```csharp
public interface IAiAnalyzerService
{
    Task<int> AnalyzeSignalsAsync(CancellationToken cancellationToken = default);
}
```

Методът връща брой обработени записи.

## Dependency injection

```csharp
public class AiAnalyzerService : IAiAnalyzerService
{
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
}
```

Регистрация в `Program.cs`:

```csharp
builder.Services.AddHttpClient<IAiAnalyzerService, AiAnalyzerService>();
```

## API key — никога hardcoded

Ключът се чете от конфигурацията при всеки run (не се кешира в поле), и се проверява веднага в началото на метода:

```csharp
var apiKey = _configuration["Anthropic:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
{
    throw new InvalidOperationException("Anthropic:ApiKey configuration is missing.");
}
```

Стойността идва от `ANTHROPIC_API_KEY` / `appsettings.Development.json` (в `.gitignore`) — виж CLAUDE.md, никога не се commit-ва в код.

## Избор на необработени сигнали + batching

Взимаш само `Signal`-и с `Processed == false`, подредени хронологично, и ги делиш на batch-ове (`Chunk`), за да не пращаш прекалено голям prompt наведнъж:

```csharp
private const int BatchSize = 15;

var unprocessedSignals = await _dbContext.Signals
    .Where(s => !s.Processed)
    .OrderBy(s => s.PublishedAt)
    .ToListAsync(cancellationToken);

foreach (var batch in unprocessedSignals.Chunk(BatchSize))
{
    var analysis = await AnalyzeBatchAsync(batch, apiKey, cancellationToken);
    if (analysis is null)
    {
        continue; // логнато вече в AnalyzeBatchAsync/ParseAnalysis, пропускаш batch-а
    }

    // persist резултата, виж по-долу

    foreach (var signal in batch)
    {
        signal.Processed = true;
    }

    await _dbContext.SaveChangesAsync(cancellationToken);
    processedCount += batch.Length;
}
```

`Signal.Processed = true` се маркира едва след успешен persist на анализа, в същия `SaveChangesAsync` — за да не изгубиш сигнал, ако анализът fail-не.

## Prompt — строг JSON изход

Prompt-ът изрично изисква Claude да върне **само** валиден JSON, с точна структура, без никакъв друг текст:

```csharp
private static string BuildPrompt(Signal[] batch)
{
    var sb = new StringBuilder();
    sb.AppendLine("You are an investment analyst. Analyze the following signals and respond with ONLY valid JSON matching this exact structure, no other text:");
    sb.AppendLine("{ \"sectors\": [ { ... } ], \"companies\": [ { ... } ] }");
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
```

За нов вид анализ: дефинирай нова точна JSON схема тук и съответен result DTO (виж по-долу) — не разчитай на свободен текст.

## Извикване на Claude API

```csharp
private const string ApiUrl = "https://api.anthropic.com/v1/messages";
private const string Model = "claude-haiku-4-5";
private const int MaxTokens = 1000;

var requestBody = new
{
    model = Model,
    max_tokens = MaxTokens,
    messages = new[] { new { role = "user", content = BuildPrompt(batch) } }
};

using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
{
    Content = JsonContent.Create(requestBody)
};
request.Headers.Add("x-api-key", apiKey);
request.Headers.Add("anthropic-version", "2023-06-01");

using var response = await _httpClient.SendAsync(request, cancellationToken);
response.EnsureSuccessStatusCode();
```

Модел, max tokens и batch size са `const` в началото на класа — лесни заtuning без да ровиш в логиката.

## Парсване на отговора

Отговорът от Messages API се deserialize-ва в минимален DTO (само `content[].type/text`, не целия response shape):

```csharp
var responseBody = await response.Content.ReadFromJsonAsync<ClaudeMessageResponse>(cancellationToken: cancellationToken);
var text = responseBody?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
if (string.IsNullOrWhiteSpace(text))
{
    _logger.LogWarning("Claude API returned no text content for a signal batch");
    return null;
}
```

Claude понякога обгражда JSON-а с обяснителен текст въпреки инструкцията — затова изрязваш между първата `{` и последната `}`, вместо да deserialize-ваш целия текст директно:

```csharp
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
```

`JsonOptions` е статичен `JsonSerializerOptions` с `JsonSerializerDefaults.Web` (camelCase), дефиниран веднъж:

```csharp
private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
```

## Обработка на грешки

За разлика от collector-ите, тук няма `RunLog` tracking — грешките на ниво batch (липсващ текст, невалиден JSON) се логват с `_logger.LogWarning` и връщат `null`, а batch-ът се пропуска (`continue`) без да маркира сигналите като `Processed`, за да се хванат при следващия run. Мрежови/HTTP грешки (`EnsureSuccessStatusCode`, connection failures) не се catch-ват локално — изключението minава нагоре към Hangfire.

Ако добавяш нов анализ, който трябва да следва RunLog конвенцията на collector-ите (напр. за да се вижда в dashboard-а колко анализа са минали/failed-нали), виж `/skills/signal-collector` за RunLog patterna и приложи го аналогично.

## Persist на резултата — private DTO класове + entity mapping

Отговорът се deserialize-ва в `private sealed class` DTO-та (не entity модели), после explicit се map-ват към EF entities:

```csharp
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
```

```csharp
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
```

`analyzedAt = DateTime.UtcNow` се взима веднъж за целия batch, за да имат всички записи от същия анализ еднакъв timestamp.

## Code template за нов вид анализ

```csharp
private const string XxxApiUrl = "https://api.anthropic.com/v1/messages";
private const string XxxModel = "claude-haiku-4-5";
private const int XxxMaxTokens = 1000;
private const int XxxBatchSize = 15;

public async Task<int> AnalyzeXxxAsync(CancellationToken cancellationToken = default)
{
    var apiKey = _configuration["Anthropic:ApiKey"];
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        throw new InvalidOperationException("Anthropic:ApiKey configuration is missing.");
    }

    var items = await _dbContext.Xxx
        .Where(/* филтър за необработени */)
        .ToListAsync(cancellationToken);

    var processedCount = 0;
    foreach (var batch in items.Chunk(XxxBatchSize))
    {
        var result = await AnalyzeXxxBatchAsync(batch, apiKey, cancellationToken);
        if (result is null)
        {
            continue;
        }

        // map result -> entities, _dbContext.Add(...)

        await _dbContext.SaveChangesAsync(cancellationToken);
        processedCount += batch.Length;
    }

    return processedCount;
}

private async Task<XxxAnalysisResult?> AnalyzeXxxBatchAsync(Xxx[] batch, string apiKey, CancellationToken cancellationToken)
{
    // построй prompt със строга JSON схема, изпрати заявка, извлечи { ... } от текста,
    // deserialize в private DTO, логни warning и върни null при провал
}
```

## Чеклист за нов analyzer

- [ ] Нов метод в `IAiAnalyzerService` + имплементация в `AiAnalyzerService`
- [ ] API key се чете от `IConfiguration` във всеки run, никога hardcoded
- [ ] Prompt изисква строг JSON формат, с точна схема в текста
- [ ] Batch-ване през `Chunk`, за да не расте prompt-ът неограничено
- [ ] JSON се извлича между първата `{` и последната `}` преди deserialize
- [ ] Невалиден/липсващ отговор → `_logger.LogWarning` + `return null` + `continue` (сигналите остават `Processed = false`)
- [ ] Успешен batch → persist entities + маркирай source записите като обработени + `SaveChangesAsync` в един и същ batch
- [ ] `dotnet build` минава чисто
