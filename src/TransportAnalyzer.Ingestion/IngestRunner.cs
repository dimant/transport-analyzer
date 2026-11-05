using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TransportAnalyzer.Domain;

namespace TransportAnalyzer.Ingestion;

public class IngestRunner(string connectionString, IRawStore raw, ILogger? logger = null)
{
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
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(p) });
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
            files = await a.FetchAsync(raw, p, ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await a.StageAsync(files, conn, p, ct);
            result = await a.NormalizeAsync(conn, runId, p, ct);
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

    /// <summary>Marks every leftover 'running' row as failed (queued or in flight when the process stopped).</summary>
    public async Task<int> AbandonRunningAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "update ingest_run set status = 'failed', finished_at = now(), error = 'abandoned at shutdown' where status = 'running'", conn);
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
        cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(files) });
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(rowCounts) });
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(unmapped) });
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
