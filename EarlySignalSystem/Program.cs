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

RecurringJob.AddOrUpdate<IAiAnalyzerService>(
    "ai-signal-analyzer",
    service => service.AnalyzeSignalsAsync(CancellationToken.None),
    "30 18 * * *");

app.Run();
