using Npgsql;

namespace TransportAnalyzer.Domain;

public sealed record SourceDescriptor(string SourceId, string Name, string Mode, string Kind /* "events" | "exposure" */,
    string Url, string Definitions, string Caveats);

public sealed record RawFile(string Name, string Path, string Sha256, DateTimeOffset FetchedAt);

public sealed record IngestParameters(IReadOnlyList<int>? Years);

/// <param name="RowCounts">Rows written per target, e.g. "event", "vehicle".</param>
/// <param name="Unmapped">Counts of unmapped source values; key format "{attribute}:{column}={value}".</param>
public sealed record NormalizeResult(IReadOnlyDictionary<string, long> RowCounts,
    IReadOnlyDictionary<string, long> Unmapped);

public interface IRawStore
{
    Task<RawFile> SaveAsync(string sourceId, string name, Stream content, CancellationToken ct);
}

public interface ISourceAdapter
{
    SourceDescriptor Descriptor { get; }
    Task<IReadOnlyList<RawFile>> FetchAsync(IRawStore raw, IngestParameters p, CancellationToken ct);
    Task StageAsync(IReadOnlyList<RawFile> files, NpgsqlConnection conn, IngestParameters p, CancellationToken ct);
    Task<NormalizeResult> NormalizeAsync(NpgsqlConnection conn, Guid runId, IngestParameters p, CancellationToken ct);
}
