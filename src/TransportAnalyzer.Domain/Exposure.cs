namespace TransportAnalyzer.Domain;

/// <summary>One denominator value (e.g. passenger-miles) for a mode, year and optional vehicle/regulation class.</summary>
public sealed record ExposureRow(string Mode, int Year, string? VehicleClass, string? RegulationClass, string Metric,
    double Value, string Provenance);

/// <summary>One line of a source-table mapping file: a source row label and the exposure key it contributes to.
/// An empty cell in the file maps to null.</summary>
public sealed record RowMap(string RowLabel, string Mode, string? VehicleClass, string? RegulationClass, string Provenance);
