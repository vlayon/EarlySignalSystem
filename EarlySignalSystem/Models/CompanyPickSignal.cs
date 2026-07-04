namespace EarlySignalSystem.Models;

public class CompanyPickSignal
{
    public int Id { get; set; }

    public int CompanyPickId { get; set; }
    public CompanyPick CompanyPick { get; set; } = null!;

    public int SignalId { get; set; }
    public Signal Signal { get; set; } = null!;
}
