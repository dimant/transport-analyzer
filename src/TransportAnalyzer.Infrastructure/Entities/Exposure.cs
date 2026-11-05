namespace TransportAnalyzer.Infrastructure.Entities;

public class Exposure
{
    public Guid ExposureId { get; set; }
    public string SourceId { get; set; } = "";
    public string Mode { get; set; } = "";
    public int Year { get; set; }
    public string? VehicleClass { get; set; }
    public string? RegulationClass { get; set; }
    public string Metric { get; set; } = "";
    public double Value { get; set; }
    public string Provenance { get; set; } = "";
    public double? StandardError { get; set; }
    public string? Assumptions { get; set; }
    public Guid IngestRunId { get; set; }
}
