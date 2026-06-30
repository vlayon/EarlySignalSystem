namespace EarlySignalSystem.Models;

public class SectorScore
{
    public int Id { get; set; }
    public string SectorName { get; set; } = string.Empty;
    public decimal Score { get; set; }
    public string? Rationale { get; set; }
    public DateTime ScoredAt { get; set; }

    public int? RunLogId { get; set; }
    public RunLog? RunLog { get; set; }
}
