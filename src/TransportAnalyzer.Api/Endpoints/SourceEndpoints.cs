using Npgsql;
using TransportAnalyzer.Domain;

namespace TransportAnalyzer.Api.Endpoints;

public static class SourceEndpoints
{
    public static void MapSourceEndpoints(this WebApplication app, string connectionString)
    {
        app.MapGet("/api/sources", async (IEnumerable<ISourceAdapter> adapters, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);

            // Latest run per source by started_at, any status.
            var lastRuns = new Dictionary<string, object>();
            await using (var cmd = new NpgsqlCommand(
                "select distinct on (source_id) source_id, ingest_run_id, status, started_at, finished_at " +
                "from ingest_run order by source_id, started_at desc", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    lastRuns[r.GetString(0)] = new
                    {
                        runId = r.GetGuid(1),
                        status = r.GetString(2),
                        startedAt = r.GetFieldValue<DateTimeOffset>(3),
                        finishedAt = r.IsDBNull(4) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(4),
                    };

            var through = new Dictionary<string, string>();
            await using (var cmd = new NpgsqlCommand(
                "select source_id, max(occurred_on)::text from event group by source_id " +
                "union all select source_id, (max(year)::text || '-12-31') from exposure group by source_id", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    if (!r.IsDBNull(1)) through[r.GetString(0)] = r.GetString(1);

            return Results.Ok(adapters.Select(a =>
            {
                var d = a.Descriptor;
                return new
                {
                    sourceId = d.SourceId,
                    name = d.Name,
                    mode = d.Mode,
                    kind = d.Kind,
                    url = d.Url,
                    definitions = d.Definitions,
                    caveats = d.Caveats,
                    lastRun = lastRuns.GetValueOrDefault(d.SourceId),
                    dataCurrentThrough = through.GetValueOrDefault(d.SourceId),
                };
            }).ToList());
        });

        app.MapGet("/api/vocabulary", async (CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select attribute, code, label, description, parent_code from vocabulary order by attribute, code", conn);
            var result = new Dictionary<string, List<object>>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var attribute = r.GetString(0);
                if (!result.TryGetValue(attribute, out var list)) result[attribute] = list = [];
                list.Add(new
                {
                    code = r.GetString(1),
                    label = r.GetString(2),
                    description = r.IsDBNull(3) ? null : r.GetString(3),
                    parentCode = r.IsDBNull(4) ? null : r.GetString(4),
                });
            }
            return Results.Ok(result);
        });
    }
}
