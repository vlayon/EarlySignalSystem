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
            "sec-edgar-collector",
            service => service.CollectSecEdgarSignalsAsync(CancellationToken.None),
            "10 18 * * *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "ted-collector",
            service => service.CollectTedSignalsAsync(CancellationToken.None),
            "15 18 * * *");

        // oecd-collector е временно деактивиран — историческите (годишни) OECD данни не са подходящи
        // за real-time signal detection. Виж CollectOecdSignalsAsync в DataCollectorService.cs.
        RecurringJob.RemoveIfExists("oecd-collector");

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
            "sec-edgar-collector",
            service => service.CollectSecEdgarSignalsAsync(CancellationToken.None),
            $"10 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "ted-collector",
            service => service.CollectTedSignalsAsync(CancellationToken.None),
            $"15 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<IDataCollectorService>(
            "esma-collector",
            service => service.CollectEsmaSignalsAsync(CancellationToken.None),
            $"25 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<IAiAnalyzerService>(
            "ai-signal-analyzer",
            service => service.AnalyzeSignalsAsync(CancellationToken.None),
            $"30 18 {dayMonth} *");

        RecurringJob.AddOrUpdate<ICumulativeScoringService>(
            "cumulative-scorer",
            service => service.CalculateScoresAsync(CancellationToken.None),
            $"0 19 {dayMonth} *");

        var restoreAt = tomorrow.AddHours(20);
        BackgroundJob.Schedule(() => RestoreDailySchedule(), restoreAt - DateTime.Now);
    }

    public static void RestoreDailySchedule() => RegisterDailyJobs();
}
