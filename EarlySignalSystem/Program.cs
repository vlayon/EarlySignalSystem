using EarlySignalSystem.Components;
using EarlySignalSystem.Data;
using EarlySignalSystem.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddScoped<AppDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

builder.Services.AddHttpClient();
builder.Services.AddHttpClient<IDataCollectorService, DataCollectorService>();
builder.Services.AddHttpClient<IAiAnalyzerService, AiAnalyzerService>();
builder.Services.AddHttpClient<IStockPriceService, StockPriceService>();
builder.Services.AddHttpClient<IOverboughtOversoldService, OverboughtOversoldService>();
builder.Services.AddHttpClient<ITickerVerificationService, TickerVerificationService>();
builder.Services.AddScoped<ICumulativeScoringService, CumulativeScoringService>();

builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));

builder.Services.AddHangfireServer();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHangfireDashboard();

app.MapPost("/api/scan-now", (IBackgroundJobClient backgroundJobs) =>
{
    // Не блокираме заявката — WaitAsync(0) връща веднага false ако gate-ът вече е зает, вместо да чака.
    if (!ScanGate.Semaphore.Wait(0))
    {
        return Results.Conflict();
    }

    try
    {
        // Hangfire OSS has no fan-in/batch continuation (that needs Hangfire Pro), so the collectors
        // are chained one after another rather than truly in parallel — this still guarantees AI
        // Analyzer runs exactly once, only after every collector has finished, and Cumulative Scorer
        // runs exactly once, only after AI Analyzer finishes.
        var eurLexJobId = backgroundJobs.Enqueue<IDataCollectorService>(
            s => s.CollectEurLexSignalsAsync(CancellationToken.None));
        var secEdgarJobId = backgroundJobs.ContinueJobWith<IDataCollectorService>(
            eurLexJobId, s => s.CollectSecEdgarSignalsAsync(CancellationToken.None));
        var secEdgar13DGJobId = backgroundJobs.ContinueJobWith<IDataCollectorService>(
            secEdgarJobId, s => s.CollectSecEdgar13DGSignalsAsync(CancellationToken.None));
        var tedJobId = backgroundJobs.ContinueJobWith<IDataCollectorService>(
            secEdgar13DGJobId, s => s.CollectTedSignalsAsync(CancellationToken.None));
        var esmaJobId = backgroundJobs.ContinueJobWith<IDataCollectorService>(
            tedJobId, s => s.CollectEsmaSignalsAsync(CancellationToken.None));
        var analyzerJobId = backgroundJobs.ContinueJobWith<IAiAnalyzerService>(
            esmaJobId, s => s.AnalyzeSignalsAsync(CancellationToken.None));
        var scorerJobId = backgroundJobs.ContinueJobWith<ICumulativeScoringService>(
            analyzerJobId, s => s.CalculateScoresAsync(CancellationToken.None));
        var technicalJobId = backgroundJobs.ContinueJobWith<IOverboughtOversoldService>(
            scorerJobId, s => s.AssessTopCompaniesAsync(CancellationToken.None));
        var tickerVerifierJobId = backgroundJobs.ContinueJobWith<ITickerVerificationService>(
            technicalJobId, s => s.VerifyPendingTickersAsync(CancellationToken.None));

        // Освобождаваме gate-а веднага щом веригата приключи успешно (последната стъпка). Hangfire OSS
        // continuation-ите по подразбиране се движат само при успех на предходната стъпка — ако някоя
        // по-ранна стъпка се провали, веригата спира по-рано и release continuation-ът никога не минава.
        // Затова добавяме и 15-минутен safety-net schedule, за да не остане gate-ът заключен завинаги.
        backgroundJobs.ContinueJobWith(tickerVerifierJobId, () => ScanGate.Release());
        backgroundJobs.Schedule(() => ScanGate.Release(), TimeSpan.FromMinutes(15));

        RecurringJobScheduler.SkipTodayAndRescheduleForTomorrow();

        return Results.Ok();
    }
    catch
    {
        ScanGate.Release();
        throw;
    }
});

RecurringJobScheduler.RegisterDailyJobs();

app.Run();
