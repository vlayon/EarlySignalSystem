namespace EarlySignalSystem.Models;

public class RunLog
{
    public int Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "Running";
    public int SignalsCollected { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Notes { get; set; }

    public ICollection<Signal> Signals { get; set; } = new List<Signal>();
    public ICollection<SectorScore> SectorScores { get; set; } = new List<SectorScore>();
    public ICollection<CompanyPick> CompanyPicks { get; set; } = new List<CompanyPick>();
}
