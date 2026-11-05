namespace TransportAnalyzer.Infrastructure.Entities;

public class Event
{
    public Guid EventId { get; set; }
    public string SourceId { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string Mode { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateOnly OccurredOn { get; set; }
    public TimeOnly? OccurredTime { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
    public string Severity { get; set; } = "";
    public int Fatalities { get; set; }
    public int SeriousInjuries { get; set; }
    public int MinorInjuries { get; set; }
    public int? GroundFatalities { get; set; }
    public int UnattributedFatalities { get; set; }
    public int UnattributedSeriousInjuries { get; set; }
    public int UnattributedMinorInjuries { get; set; }
    public string? OperationClass { get; set; }
    public string? Phase { get; set; }
    public string? WeatherSummary { get; set; }
    public string? Narrative { get; set; }
    public string? ProbableCause { get; set; }
    public string? InvestigationStatus { get; set; }
    public string SourcePayload { get; set; } = "{}";
    public Guid IngestRunId { get; set; }
    public List<Vehicle> Vehicles { get; set; } = [];
}
