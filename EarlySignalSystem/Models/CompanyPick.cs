namespace EarlySignalSystem.Models;

public class CompanyPick
{
    public int Id { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string Sector { get; set; } = string.Empty;
    public decimal ConfidenceScore { get; set; }
    public string? Rationale { get; set; }
    public DateTime PickedAt { get; set; }

    public int? RunLogId { get; set; }
    public RunLog? RunLog { get; set; }
}
