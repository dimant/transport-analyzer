namespace TransportAnalyzer.Ingestion;

/// <summary>Bound from the "Ingest" configuration section.</summary>
public sealed class IngestOptions
{
    /// <summary>A 'running' run older than this is treated as dead and failed when a new run is requested.</summary>
    public TimeSpan StaleRunAfter { get; set; } = TimeSpan.FromHours(6);
}
