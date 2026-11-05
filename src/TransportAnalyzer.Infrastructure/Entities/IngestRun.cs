namespace TransportAnalyzer.Infrastructure.Entities;

public class IngestRun
{
    public Guid IngestRunId { get; set; }
    public string SourceId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "running";
    public string Parameters { get; set; } = "{}";
    public string RawFiles { get; set; } = "[]";
    public string RowCounts { get; set; } = "{}";
    public string Unmapped { get; set; } = "{}";
    public string? Error { get; set; }
}
