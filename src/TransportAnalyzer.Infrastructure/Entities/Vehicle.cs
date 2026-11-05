namespace TransportAnalyzer.Infrastructure.Entities;

public class Vehicle
{
    public Guid VehicleId { get; set; }
    public Guid EventId { get; set; }
    public int VehicleNo { get; set; }
    public string? Registration { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public int? YearBuilt { get; set; }
    public string? Damage { get; set; }
    public string VehicleClass { get; set; } = "unknown";
    public string OperatorType { get; set; } = "unknown";
    public string RegulationClass { get; set; } = "unknown";
    public string MaintenanceRegime { get; set; } = "unknown";
    public int? Occupants { get; set; }
    public int? CrewCount { get; set; }
    public int? VehicleAgeYears { get; set; }
    public int OccupantFatalities { get; set; }
    public int OccupantSeriousInjuries { get; set; }
    public int OccupantMinorInjuries { get; set; }
    public int NonoccupantFatalities { get; set; }
    public int NonoccupantSeriousInjuries { get; set; }
    public int NonoccupantMinorInjuries { get; set; }
    public string SourcePayload { get; set; } = "{}";
    public Guid IngestRunId { get; set; }
}
