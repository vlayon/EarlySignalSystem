namespace EarlySignalSystem.Models;

public class ShortlistSnapshot
{
    public int Id { get; set; }
    public DateTime ScanDate { get; set; }
    public int ScanNumber { get; set; }
    public int CompanyRank { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string? Sector { get; set; }
    public decimal CumulativeScore { get; set; }
    public string? Sentiment { get; set; }
    public int SignalCount { get; set; }
    public int SignalDiversity { get; set; }
    public VelocityLevel VelocityLevel { get; set; }
    public string? Rationale { get; set; }
    public DateTime CreatedAt { get; set; }
}
