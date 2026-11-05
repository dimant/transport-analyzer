using Npgsql;
using TransportAnalyzer.Api.Query;

namespace TransportAnalyzer.Api.Endpoints;

public static class EventEndpoints
{
    public static void MapEventEndpoints(this WebApplication app, string connectionString)
    {
        app.MapGet("/api/events/aggregate", async (HttpRequest request, CancellationToken ct) =>
        {
            var parsed = AggregateQuery.Parse(request.Query);
            if (!parsed.IsSuccess)
                return Results.BadRequest(new { error = parsed.Error.error, parameter = parsed.Error.parameter });

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            return Results.Ok(await parsed.Value.RunAsync(conn, ct));
        });

        app.MapGet("/api/events/sample", async (HttpRequest request, CancellationToken ct) =>
        {
            var parsed = SampleQuery.Parse(request.Query);
            if (!parsed.IsSuccess)
                return Results.BadRequest(new { error = parsed.Error.error, parameter = parsed.Error.parameter });

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            return Results.Ok(await parsed.Value.RunAsync(conn, ct));
        });
    }
}
