# Skill: Нов Signal Collector

Използвай този skill, когато добавяш нов източник за сигнали (нов RSS feed, ново API — SEC EDGAR, Financial Modeling Prep, OECD, Reuters RSS и т.н.).

## Къде живее кодът

Няма отделен клас per source. Всички collector-и се събират в `Services/DataCollectorService.cs`, имплементиращ `Services/IDataCollectorService.cs` — по един public метод на източник (напр. `CollectEurLexSignalsAsync`). Нов източник = нов метод в същия клас/интерфейс, не нов файл.

## Структура на интерфейса

```csharp
public interface IDataCollectorService
{
    Task<int> CollectEurLexSignalsAsync(CancellationToken cancellationToken = default);
    Task<int> CollectXxxSignalsAsync(CancellationToken cancellationToken = default);
}
```

Методът връща брой новосъбрани (не-дублирани) сигнали.

## Dependency injection

`DataCollectorService` получава `AppDbContext` и `HttpClient` директно през конструктора (без Repository pattern — виж CLAUDE.md):

```csharp
public class DataCollectorService : IDataCollectorService
{
    private readonly AppDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DataCollectorService> _logger;

    public DataCollectorService(AppDbContext dbContext, HttpClient httpClient, ILogger<DataCollectorService> logger)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _logger = logger;
    }
}
```

Регистрация в `Program.cs` (`AddHttpClient` вместо `AddScoped`, за да получи типизиран `HttpClient` с connection pooling):

```csharp
builder.Services.AddHttpClient<IDataCollectorService, DataCollectorService>();
```

## RunLog — проследяване на всеки run

Всеки collect метод създава `RunLog` в началото, преди да прави каквото и да е I/O, и записва веднага, за да получи `Id`:

```csharp
var runLog = new RunLog
{
    StartedAt = DateTime.UtcNow,
    Status = "Running"
};
_dbContext.RunLogs.Add(runLog);
await _dbContext.SaveChangesAsync(cancellationToken);
```

Всеки нов `Signal` се свързва с текущия run през `RunLogId = runLog.Id`.

При успех:

```csharp
runLog.Status = "Completed";
runLog.SignalsCollected = collected;
runLog.CompletedAt = DateTime.UtcNow;
await _dbContext.SaveChangesAsync(cancellationToken);
```

## Дедупликация по SourceUrl

Преди да добавиш нов `Signal`, събери вече съществуващите `SourceUrl` за конкретния `Source` в `HashSet`, за да проверяваш O(1) в паметта вместо да питаш базата за всеки item:

```csharp
var existingLinks = await _dbContext.Signals
    .Where(s => s.Source == SourceName)
    .Select(s => s.SourceUrl)
    .ToHashSetAsync(cancellationToken);

foreach (var item in items)
{
    if (existingLinks.Contains(item.Link))
    {
        continue;
    }

    _dbContext.Signals.Add(new Signal { /* ... */ SourceUrl = item.Link, /* ... */ });
    existingLinks.Add(item.Link); // за да не се добави пак в същия batch
    collected++;
}
```

## Обработка на грешки

Целият fetch + persist блок е в `try/catch`. При грешка: логваш през `ILogger`, маркираш `RunLog` като `Failed` с `ErrorMessage`, и хвърляш изключението нагоре (Hangfire ще прихване retry-a):

```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to collect Xxx signals");
    runLog.Status = "Failed";
    runLog.ErrorMessage = ex.Message;
    runLog.CompletedAt = DateTime.UtcNow;
    await _dbContext.SaveChangesAsync(cancellationToken);
    throw;
}
```

## Fetch логика

Специфичната за източника логика (RSS parsing с `XDocument`, JSON API deserialization и т.н.) се изолира в private helper метод, който връща прост `record` (или списък от такива), а не directly пипа `DbContext` — така основният метод остава четим и следва еднакъв shape за всички collector-и.

## Code template за нов collector

```csharp
private const string XxxSource = "Xxx";
private const string XxxSignalType = "Xxx";
private const string XxxApiUrl = "https://...";

public async Task<int> CollectXxxSignalsAsync(CancellationToken cancellationToken = default)
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
        var items = await FetchXxxItemsAsync(cancellationToken);

        var existingLinks = await _dbContext.Signals
            .Where(s => s.Source == XxxSource)
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
                Source = XxxSource,
                SignalType = XxxSignalType,
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
        _logger.LogError(ex, "Failed to collect Xxx signals");
        runLog.Status = "Failed";
        runLog.ErrorMessage = ex.Message;
        runLog.CompletedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        throw;
    }

    return collected;
}

private async Task<List<XxxFeedItem>> FetchXxxItemsAsync(CancellationToken cancellationToken)
{
    // HTTP call през _httpClient, parse response, връщаш List<XxxFeedItem>
}

private sealed record XxxFeedItem(string Title, string Link, string? Description, DateTime PublishedAt);
```

## Wiring в Program.cs

Не забравяй да добавиш recurring Hangfire job за новия collector, аналогично на съществуващия:

```csharp
RecurringJob.AddOrUpdate<IDataCollectorService>(
    "xxx-data-collector",
    service => service.CollectXxxSignalsAsync(CancellationToken.None),
    "0 18 * * *");
```

## Чеклист за нов collector

- [ ] Нов метод в `IDataCollectorService` + имплементация в `DataCollectorService`
- [ ] `RunLog` се създава преди fetch-а и се update-ва при успех/грешка
- [ ] Дедупликация по `SourceUrl` през `HashSet`, зареден веднъж преди цикъла
- [ ] `try/catch` около целия блок, грешка → `RunLog.Status = "Failed"` + `ErrorMessage`, после `throw`
- [ ] Fetch логиката е в private helper, връщащ прост `record`
- [ ] Добавен `RecurringJob.AddOrUpdate` в `Program.cs`
- [ ] `dotnet build` минава чисто
