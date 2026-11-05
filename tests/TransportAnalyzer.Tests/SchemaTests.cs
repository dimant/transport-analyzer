using Npgsql;

namespace TransportAnalyzer.Tests;

[Collection("db")]
public class SchemaTests(PostgresFixture db)
{
    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    [Fact]
    public async Task Migration_CreatesNormalizedTables()
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema='public'", conn);
        var tables = new HashSet<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) tables.Add(r.GetString(0));
        foreach (var t in new[] { "event", "vehicle", "exposure", "ingest_run", "vocabulary", "vocabulary_map", "operator", "event_operator" })
            Assert.Contains(t, tables);
    }

    [Fact]
    public async Task Vocabulary_LightTruckParentIsLightDuty()
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT parent_code FROM vocabulary WHERE attribute='vehicle_class' AND code='light_truck'", conn);
        Assert.Equal("light_duty", (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Exposure_NullKeysAreNotDistinct()
    {
        await using var conn = await OpenAsync();
        const string sql = """
            INSERT INTO exposure (exposure_id, source_id, mode, year, vehicle_class, regulation_class, metric, value, provenance, ingest_run_id)
            VALUES (gen_random_uuid(), 'test', 'highway', 2020, NULL, NULL, 'vehicle_miles', 1, 'measured', gen_random_uuid())
            """;
        await using (var first = new NpgsqlCommand(sql, conn)) await first.ExecuteNonQueryAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var second = new NpgsqlCommand(sql, conn);
            await second.ExecuteNonQueryAsync();
        });
        Assert.Equal("23505", ex.SqlState);
    }
}
