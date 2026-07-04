namespace EarlySignalSystem.Models;

public class CumulativeScore
{
    public int Id { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string? Sector { get; set; }
    public decimal Score { get; set; }
    public int SignalCount { get; set; }
    public int SignalDiversity { get; set; }
    public VelocityLevel VelocityLevel { get; set; }
    public DateTime LastCalculatedAt { get; set; }
}
