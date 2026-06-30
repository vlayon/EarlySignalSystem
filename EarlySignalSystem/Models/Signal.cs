namespace EarlySignalSystem.Models;

public class Signal
{
    public int Id { get; set; }
    public string Source { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? RawContent { get; set; }
    public string? Sector { get; set; }
    public string? Ticker { get; set; }
    public DateTime PublishedAt { get; set; }
    public DateTime CollectedAt { get; set; }
    public bool Processed { get; set; }

    public int? RunLogId { get; set; }
    public RunLog? RunLog { get; set; }
}
