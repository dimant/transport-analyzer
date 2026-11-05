using System.Security.Cryptography;
using System.Text;
using Npgsql;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Ingestion;

namespace TransportAnalyzer.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app, string connectionString)
    {
        var admin = app.MapGroup("/api/admin");
        admin.AddEndpointFilter(async (ctx, next) =>
        {
            var token = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Admin:Token"];
            if (!string.IsNullOrEmpty(token))
            {
                var supplied = Encoding.UTF8.GetBytes(ctx.HttpContext.Request.Headers["X-Admin-Token"].ToString());
                if (!CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes(token)))
                    return Results.Unauthorized();
            }
            return await next(ctx);
        });

        admin.MapPost("/ingest/{sourceId}", async (string sourceId, string? years, IngestQueue queue) =>
        {
            if (!queue.IsKnownSource(sourceId)) return Results.NotFound();
            List<int>? parsed = null;
            if (!string.IsNullOrWhiteSpace(years))
            {
                parsed = [];
                foreach (var part in years.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(part, out var y)) return Results.BadRequest(new { error = $"invalid year '{part}'" });
                    parsed.Add(y);
                }
            }
            var (started, runId) = await queue.TryEnqueueAsync(sourceId, new IngestParameters(parsed));
            return started
                ? Results.Accepted(value: new { runId })
                : Results.Json(new { error = $"a run for '{sourceId}' is already running", runId }, statusCode: StatusCodes.Status409Conflict);
        });

        admin.MapGet("/runs", async (string? sourceId, int? limit, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "select ingest_run_id, source_id, started_at, finished_at, status, parameters::text, raw_files::text, " +
                "row_counts::text, unmapped::text, error from ingest_run " +
                "where (@src::text is null or source_id = @src) order by started_at desc limit @lim", conn);
            cmd.Parameters.AddWithValue("src", (object?)sourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("lim", Math.Clamp(limit ?? 50, 1, 500));
            var runs = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                runs.Add(new
                {
                    runId = r.GetGuid(0),
                    sourceId = r.GetString(1),
                    startedAt = r.GetFieldValue<DateTimeOffset>(2),
                    finishedAt = r.IsDBNull(3) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(3),
                    status = r.GetString(4),
                    parameters = System.Text.Json.JsonDocument.Parse(r.GetString(5)).RootElement,
                    rawFiles = System.Text.Json.JsonDocument.Parse(r.GetString(6)).RootElement,
                    rowCounts = System.Text.Json.JsonDocument.Parse(r.GetString(7)).RootElement,
                    unmapped = System.Text.Json.JsonDocument.Parse(r.GetString(8)).RootElement,
                    error = r.IsDBNull(9) ? null : r.GetString(9),
                });
            return Results.Ok(runs);
        });
    }
}
