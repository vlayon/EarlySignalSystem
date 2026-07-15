namespace EarlySignalSystem.Models;

public class TechnicalAssessment
{
    public int Id { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string Assessment { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public decimal? RSI { get; set; }
    public decimal? MACDSignal { get; set; }
    public DateTime AssessedAt { get; set; }
}
