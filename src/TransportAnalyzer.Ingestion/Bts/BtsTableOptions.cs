namespace TransportAnalyzer.Ingestion.Bts;

/// <summary>One BTS National Transportation Statistics table. Bound from configuration section
/// <c>Sources:{SourceId}</c> over the defaults from <see cref="PassengerMiles"/> or <see cref="VehicleMiles"/>.</summary>
public sealed class BtsTableOptions
{
    public string SourceId { get; set; } = "";
    /// <summary>Human-facing bts.gov page of the table; informational only, nothing is fetched from it.</summary>
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Metric written to <c>exposure.metric</c>.</summary>
    public string Metric { get; set; } = "";
    /// <summary>Factor from table units to base units (the tables are in millions).</summary>
    public double UnitMultiplier { get; set; } = 1_000_000;
    /// <summary>Mapping file name under <c>{Data:Root}/mappings</c>.</summary>
    public string MappingFile { get; set; } = "";
    /// <summary>Drop folder holding the manually downloaded xlsx; set from <c>Inbox:Root</c> at registration.</summary>
    public string InboxDir { get; set; } = "";
    /// <summary>Table title used in the "file missing" message.</summary>
    public string TableLabel { get; set; } = "";

    public static BtsTableOptions PassengerMiles() => new()
    {
        SourceId = "bts-passenger-miles",
        Url = "https://www.bts.gov/content/us-passenger-miles",
        Name = "BTS National Transportation Statistics, Table 1-40 U.S. Passenger-Miles",
        Metric = "passenger_miles",
        MappingFile = "bts-passenger-miles.csv",
        TableLabel = "NTS Table 1-40 'U.S. Passenger-Miles'",
    };

    public static BtsTableOptions VehicleMiles() => new()
    {
        SourceId = "bts-vehicle-miles",
        Url = "https://www.bts.gov/content/us-vehicle-miles",
        Name = "BTS National Transportation Statistics, Table 1-35 U.S. Vehicle-Miles",
        Metric = "vehicle_miles",
        MappingFile = "bts-vehicle-miles.csv",
        TableLabel = "NTS Table 1-35 'U.S. Vehicle-Miles'",
    };
}
