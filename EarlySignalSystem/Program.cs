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

builder.Services.AddHttpClient<IDataCollectorService, DataCollectorService>();
builder.Services.AddHttpClient<IAiAnalyzerService, AiAnalyzerService>();
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

RecurringJob.AddOrUpdate<IDataCollectorService>(
    "eur-lex-data-collector",
    service => service.CollectEurLexSignalsAsync(CancellationToken.None),
    "0 18 * * *");

RecurringJob.AddOrUpdate<IDataCollectorService>(
    "sec-edgar-collector",
    service => service.CollectSecEdgarSignalsAsync(CancellationToken.None),
    "10 18 * * *");

RecurringJob.AddOrUpdate<IDataCollectorService>(
    "ted-collector",
    service => service.CollectTedSignalsAsync(CancellationToken.None),
    "15 18 * * *");

RecurringJob.AddOrUpdate<IDataCollectorService>(
    "oecd-collector",
    service => service.CollectOecdSignalsAsync(CancellationToken.None),
    "20 18 * * *");

RecurringJob.AddOrUpdate<IDataCollectorService>(
    "esma-collector",
    service => service.CollectEsmaSignalsAsync(CancellationToken.None),
    "25 18 * * *");

RecurringJob.AddOrUpdate<IAiAnalyzerService>(
    "ai-signal-analyzer",
    service => service.AnalyzeSignalsAsync(CancellationToken.None),
    "30 18 * * *");

RecurringJob.AddOrUpdate<ICumulativeScoringService>(
    "cumulative-scorer",
    service => service.CalculateScoresAsync(CancellationToken.None),
    "0 19 * * *");

app.Run();
