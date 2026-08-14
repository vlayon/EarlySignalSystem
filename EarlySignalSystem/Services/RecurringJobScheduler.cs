using Hangfire;

namespace EarlySignalSystem.Services;

public static class RecurringJobScheduler
{
    public static void RegisterDailyJobs()
    {
        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "eur-lex-data-collector",
            service => service.CollectEurLexSignalsAsync(CancellationToken.None),
            "0 18 * * *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "ep-parliament-collector",
            service => service.CollectEpParliamentSignalsAsync(CancellationToken.None),
            "5 18 * * *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "sec-edgar-collector",
            service => service.CollectSecEdgarSignalsAsync(CancellationToken.None),
            "10 18 * * *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "sec-edgar-13dg-collector",
            service => service.CollectSecEdgar13DGSignalsAsync(CancellationToken.None),
            "12 18 * * *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "ted-collector",
            service => service.CollectTedSignalsAsync(CancellationToken.None),
            "15 18 * * *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "oecd-collector",
            service => service.CollectOecdSignalsAsync(CancellationToken.None),
            "20 18 * * *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "amf-collector",
            service => service.CollectAmfSignalsAsync(CancellationToken.None),
            "25 18 * * *");

        RecurringJob.AddOrUpdate<IAiAnalyzerService>(
            "ai-signal-analyzer",
            service => service.AnalyzeSignalsAsync(CancellationToken.None),
            "30 18 * * *");

        // ticker-verifier е ПРЕДИ cumulative-scorer нарочно — виж коментара в Program.cs /api/scan-now
        // веригата. AI Analyzer-ът току-що създаде нови компании без тикър; ако Scorer-ът мине първи,
        // днешните нови компании остават без тикър/цена цял ден до утрешния цикъл.
        RecurringJob.AddOrUpdate<ITickerVerificationService>(
            "ticker-verifier",
            service => service.VerifyPendingTickersAsync(CancellationToken.None),
            "45 18 * * *");

        RecurringJob.AddOrUpdate<ICumulativeScoringService>(
            "cumulative-scorer",
            service => service.CalculateScoresAsync(CancellationToken.None),
            "0 19 * * *");

        RecurringJob.AddOrUpdate<IOverboughtOversoldService>(
            "technical-assessor",
            service => service.AssessTopCompaniesAsync(CancellationToken.None),
            "15 19 * * *");
    }

    // "Scan Now" вече изпълни ръчно днешния цикъл — пренасрочваме всеки recurring job да гръмне
    // само утре (cron, закачен за конкретни ден+месец), за да не се дублира сканирането по-късно
    // същия ден. RestoreDailySchedule се самопланира да върне нормалния ежедневен cron след утрешния run.
    public static void SkipTodayAndRescheduleForTomorrow()
    {
        var tomorrow = DateTime.Today.AddDays(1);
        var dayMonth = $"{tomorrow.Day} {tomorrow.Month}";

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "eur-lex-data-collector",
            service => service.CollectEurLexSignalsAsync(CancellationToken.None),
            $"0 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "ep-parliament-collector",
            service => service.CollectEpParliamentSignalsAsync(CancellationToken.None),
            $"5 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "sec-edgar-collector",
            service => service.CollectSecEdgarSignalsAsync(CancellationToken.None),
            $"10 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "sec-edgar-13dg-collector",
            service => service.CollectSecEdgar13DGSignalsAsync(CancellationToken.None),
            $"12 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "ted-collector",
            service => service.CollectTedSignalsAsync(CancellationToken.None),
            $"15 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "oecd-collector",
            service => service.CollectOecdSignalsAsync(CancellationToken.None),
            $"20 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "amf-collector",
            service => service.CollectAmfSignalsAsync(CancellationToken.None),
            $"25 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<IAiAnalyzerService>(
            "ai-signal-analyzer",
            service => service.AnalyzeSignalsAsync(CancellationToken.None),
            $"30 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<ITickerVerificationService>(
            "ticker-verifier",
            service => service.VerifyPendingTickersAsync(CancellationToken.None),
            $"45 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<ICumulativeScoringService>(
            "cumulative-scorer",
            service => service.CalculateScoresAsync(CancellationToken.None),
            $"0 19 {dayMonth} *");

        RecurringJob.AddOrUpdate<IOverboughtOversoldService>(
            "technical-assessor",
            service => service.AssessTopCompaniesAsync(CancellationToken.None),
            $"15 19 {dayMonth} *");

        var restoreAt = tomorrow.AddHours(20);
        BackgroundJob.Schedule(() => RestoreDailySchedule(), restoreAt - DateTime.Now);
    }

    public static void RestoreDailySchedule() => RegisterDailyJobs();
}
