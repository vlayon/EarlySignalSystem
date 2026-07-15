using EarlySignalSystem.Models;

namespace EarlySignalSystem.Services;

public interface IOverboughtOversoldService
{
    Task<TechnicalAssessment?> AssessAsync(string ticker, decimal? rsi = null, CancellationToken cancellationToken = default);

    // Hangfire recurring job entry point — не приема ticker (recurring job-овете нямат аргументи),
    // затова итерира вътрешно топ 5 компании и извиква AssessAsync за всяка. Виж RecurringJobScheduler.
    Task<int> AssessTopCompaniesAsync(CancellationToken cancellationToken = default);
}
