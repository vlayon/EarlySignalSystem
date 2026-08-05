namespace EarlySignalSystem.Models;

public class Company
{
    public int Id { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string? Ticker { get; set; }
    // AI Analyzer-ът предлага hint за тикъра при създаване на компанията — НЕ е верифициран,
    // само подсказва по-прецизна ключова дума за търсене в TickerVerificationService.
    public string? TickerHint { get; set; }
    public string? Exchange { get; set; }
    public string? Country { get; set; }
    public string? Sector { get; set; }
    public bool TickerVerified { get; set; }
    public DateTime? TickerVerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
