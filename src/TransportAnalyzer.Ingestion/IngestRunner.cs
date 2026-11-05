using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TransportAnalyzer.Domain;

namespace TransportAnalyzer.Ingestion;

public class IngestRunner(string connectionString, IRawStore raw, ILogger? logger = null)
{
    /// <summary>R24: persisted JSON uses camelCase object keys. Dictionary keys (row counts, unmapped) are data and
    /// are written as given.</summary>
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    /// <summary>Inserts a run row with status 'running' and returns its id.</summary>
    public async Task<Guid> CreateRunAsync(string sourceId, IngestParameters p, CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "insert into ingest_run (ingest_run_id, source_id, started_at, status, parameters, raw_files, row_counts, unmapped) " +
            "values (@id, @src, now(), 'running', @p, '[]'::jsonb, '{}'::jsonb, '{}'::jsonb)", conn);
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("src", sourceId);
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(p, Json) });
        await cmd.ExecuteNonQueryAsync(ct);
        return runId;
    }

    public async Task<Guid> RunAsync(ISourceAdapter a, IngestParameters p, CancellationToken ct)
    {
        var runId = await CreateRunAsync(a.Descriptor.SourceId, p, ct);
        await ExecuteAsync(a, p, runId, ct);
        return runId;
    }

    /// <summary>Runs Fetch, Stage and Normalize for an existing 'running' row and records the outcome.</summary>
    public async Task ExecuteAsync(ISourceAdapter a, IngestParameters p, Guid runId, CancellationToken ct)
    {
        IReadOnlyList<RawFile> files = [];
        NormalizeResult result;
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            files = await a.FetchAsync(new DedupingRawStore(raw, connectionString, logger), p, ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await a.StageAsync(files, conn, p, ct);
            result = await a.NormalizeAsync(conn, runId, p, ct);
            // Inside the transaction: the counts describe exactly the rows this commit makes visible, and a
            // counting failure fails the run instead of leaving a succeeded run without its quality report.
            result = result with { RowCounts = await WithUnknownCountsAsync(conn, runId, result.RowCounts, ct) };
            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            try
            {
                await FinishAsync(runId, "failed", files, new Dictionary<string, long>(), new Dictionary<string, long>(),
                    $"{ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception finishEx)
            {
                // The run row stays 'running' until the next startup sweep; keep both causes in the log.
                logger?.LogError(new AggregateException(ex, finishEx),
                    "Ingest run {RunId} failed and its failed status could not be recorded", runId);
            }
            return;
        }

        // Data is committed from here on; a status-write problem must never turn into a 'failed' record.
        try
        {
            await FinishAsync(runId, "succeeded", files, result.RowCounts, result.Unmapped, null);
        }
        catch (Exception finishEx)
        {
            logger?.LogError(finishEx,
                "Ingest run {RunId} committed but its succeeded status could not be recorded", runId);
        }
    }

    /// <summary>Harmonized vehicle attributes whose <c>unknown</c> share is reported per run (spec section 5).</summary>
    private static readonly string[] VehicleAttributes =
        ["vehicle_class", "operator_type", "regulation_class", "maintenance_regime"];

    /// <summary>Adds <c>"unknown:{attribute}"</c> counts over the vehicles stamped with this run, plus a
    /// <c>"vehicle"</c> total when the adapter gave none. A run that wrote no vehicles adds nothing.</summary>
    private static async Task<IReadOnlyDictionary<string, long>> WithUnknownCountsAsync(NpgsqlConnection conn, Guid runId,
        IReadOnlyDictionary<string, long> rowCounts, CancellationToken ct)
    {
        var filters = string.Join(", ", VehicleAttributes.Select(c => $"count(*) filter (where {c} = 'unknown')"));
        await using var cmd = new NpgsqlCommand($"select count(*), {filters} from vehicle where ingest_run_id = @run", conn);
        cmd.Parameters.AddWithValue("run", runId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var total = r.GetInt64(0);
        if (total == 0) return rowCounts;
        var counts = new Dictionary<string, long>(rowCounts);
        counts.TryAdd("vehicle", total);
        for (var i = 0; i < VehicleAttributes.Length; i++)
            counts[$"unknown:{VehicleAttributes[i]}"] = r.GetInt64(i + 1);
        return counts;
    }

    /// <summary>Marks every leftover 'running' row as failed (queued or in flight when the process stopped).</summary>
    public async Task<int> AbandonRunningAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "update ingest_run set status = 'failed', finished_at = now(), error = 'abandoned at shutdown' where status = 'running'", conn);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Fails the source's 'running' rows started more than <paramref name="after"/> ago.</summary>
    public async Task<int> FailStaleAsync(string sourceId, TimeSpan after, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "update ingest_run set status = 'failed', finished_at = now(), error = 'stale: exceeded Ingest:StaleRunAfter' " +
            "where source_id = @s and status = 'running' and started_at < now() - @after", conn);
        cmd.Parameters.AddWithValue("s", sourceId);
        cmd.Parameters.AddWithValue("after", after);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Id of a run for the source that is still 'running', if any.</summary>
    public async Task<Guid?> FindRunningAsync(string sourceId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select ingest_run_id from ingest_run where source_id = @s and status = 'running' order by started_at limit 1", conn);
        cmd.Parameters.AddWithValue("s", sourceId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    protected virtual async Task FinishAsync(Guid runId, string status, IReadOnlyList<RawFile> files,
        IReadOnlyDictionary<string, long> rowCounts, IReadOnlyDictionary<string, long> unmapped, string? error)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(CancellationToken.None);
        await using var cmd = new NpgsqlCommand(
            "update ingest_run set finished_at = now(), status = @s, raw_files = @f, row_counts = @c, unmapped = @u, error = @e " +
            "where ingest_run_id = @id", conn);
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(files, Json) });
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(rowCounts, Json) });
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(unmapped, Json) });
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Saves through <paramref name="inner"/>, then, when the new file's sha256 equals that of the same-named raw
    /// file of the source's most recent succeeded run that has one, and that file is still on disk, deletes the new copy
    /// (and its directory if left empty) and returns the existing path. Identical downloads are thus stored once.</summary>
    private sealed class DedupingRawStore(IRawStore inner, string connectionString, ILogger? logger) : IRawStore
    {
        public async Task<RawFile> SaveAsync(string sourceId, string name, Stream content, CancellationToken ct)
        {
            var saved = await inner.SaveAsync(sourceId, name, content, ct);
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                select f->>'path', f->>'sha256'
                from ingest_run r cross join lateral jsonb_array_elements(r.raw_files) f
                where r.source_id = @s and r.status = 'succeeded' and f->>'name' = @n
                order by r.finished_at desc nulls last, r.started_at desc
                limit 1
                """, conn);
            cmd.Parameters.AddWithValue("s", sourceId);
            cmd.Parameters.AddWithValue("n", name);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.IsDBNull(0) || r.IsDBNull(1)) return saved;
            var (previousPath, previousSha) = (r.GetString(0), r.GetString(1));
            if (previousSha != saved.Sha256 || previousPath == saved.Path || !File.Exists(previousPath)) return saved;

            try
            {
                File.Delete(saved.Path);
                var dir = Path.GetDirectoryName(saved.Path);
                if (dir is not null && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Could not delete duplicate raw file {Path}", saved.Path);
            }
            logger?.LogInformation("Raw file {Name} of {SourceId} is unchanged (sha256 {Sha}); reusing {Path}",
                name, sourceId, saved.Sha256, previousPath);
            return saved with { Path = previousPath };
        }
    }
}
