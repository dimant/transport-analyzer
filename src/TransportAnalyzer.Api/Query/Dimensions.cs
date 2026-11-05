namespace TransportAnalyzer.Api.Query;

public enum Grain { Event, Vehicle }

/// <summary>A whitelisted query dimension: its public name, the SQL expression it maps to, the coarsest grain at
/// which it exists, and whether its values are integers.</summary>
public sealed record Dimension(string Name, string Sql, Grain Grain, bool IsInteger = false);

/// <summary>The only names a request may group or filter by. SQL aliases: <c>e</c> event, <c>v</c> vehicle,
/// <c>vc</c> the vehicle_class vocabulary row (left joined at vehicle grain).</summary>
public static class Dimensions
{
    public static readonly IReadOnlyDictionary<string, Dimension> All = new Dimension[]
    {
        new("source_id", "e.source_id", Grain.Event),
        new("mode", "e.mode", Grain.Event),
        new("event_type", "e.event_type", Grain.Event),
        new("severity", "e.severity", Grain.Event),
        new("state", "e.state", Grain.Event),
        new("country", "e.country", Grain.Event),
        new("operation_class", "e.operation_class", Grain.Event),
        new("phase", "e.phase", Grain.Event),
        new("vehicle_class", "v.vehicle_class", Grain.Vehicle),
        new("vehicle_class_parent", "coalesce(vc.parent_code, v.vehicle_class)", Grain.Vehicle),
        new("operator_type", "v.operator_type", Grain.Vehicle),
        new("regulation_class", "v.regulation_class", Grain.Vehicle),
        new("maintenance_regime", "v.maintenance_regime", Grain.Vehicle),
        new("occupants", "v.occupants", Grain.Vehicle, IsInteger: true),
        new("crew_count", "v.crew_count", Grain.Vehicle, IsInteger: true),
        new("vehicle_age_years", "v.vehicle_age_years", Grain.Vehicle, IsInteger: true),
        new("year_built", "v.year_built", Grain.Vehicle, IsInteger: true),
        new("make", "v.make", Grain.Vehicle),
    }.ToDictionary(d => d.Name, StringComparer.Ordinal);
}
