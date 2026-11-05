using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;
using TransportAnalyzer.Ingestion;

namespace TransportAnalyzer.Tests;

[Collection("db")]
public class SourceEndpointsTests(PostgresFixture db)
{
    private sealed class StubAdapter(string sourceId) : ISourceAdapter
    {
        public SourceDescriptor Descriptor { get; } =
            new(sourceId, "Stub", "road", "events", "https://example.test", "defs", "caveats");

        public Task<IReadOnlyList<RawFile>> FetchAsync(IRawStore raw, IngestParameters p, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task StageAsync(IReadOnlyList<RawFile> files, NpgsqlConnection conn, IngestParameters p, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<NormalizeResult> NormalizeAsync(NpgsqlConnection conn, Guid runId, IngestParameters p, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Sources_ListsRegisteredAdaptersWithNullsBeforeIngest()
    {
        var stubId = "t7-" + Guid.NewGuid().ToString("N");
        await using var factory = new ApiFactory(db, null, new StubAdapter(stubId));
        var sources = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/sources");

        var ids = sources.EnumerateArray().Select(s => s.GetProperty("sourceId").GetString()).ToList();
        Assert.Contains("fars", ids);
        Assert.Contains("bts-passenger-miles", ids);
        Assert.Contains("bts-vehicle-miles", ids);
        var stub = sources.EnumerateArray().Single(s => s.GetProperty("sourceId").GetString() == stubId);
        Assert.Equal(JsonValueKind.Null, stub.GetProperty("lastRun").ValueKind);
        Assert.Equal(JsonValueKind.Null, stub.GetProperty("dataCurrentThrough").ValueKind);
        Assert.Equal("events", stub.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Sources_AfterFarsFixtureIngest_ReportsCurrentThrough()
    {
        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var del = new NpgsqlCommand(
                "delete from event where source_id = 'fars' and source_key like '2022-%'", conn);
            await del.ExecuteNonQueryAsync();
        }
        await using var factory = new ApiFactory(db, null, FarsTestSource.Adapter(FarsTestSource.BuildZip(2022)));
        await new IngestRunner(db.ConnectionString, new RawStore(factory.RawRoot))
            .RunAsync(FarsTestSource.Adapter(FarsTestSource.BuildZip(2022)), new IngestParameters([2022]), CancellationToken.None);

        var sources = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/sources");
        var fars = sources.EnumerateArray().Single(s => s.GetProperty("sourceId").GetString() == "fars");
        Assert.Equal("2022-11-20", fars.GetProperty("dataCurrentThrough").GetString());
        Assert.Equal("succeeded", fars.GetProperty("lastRun").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Vocabulary_ReturnsParentCodes()
    {
        await using var factory = new ApiFactory(db);
        var vocab = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/vocabulary");
        var car = vocab.GetProperty("vehicle_class").EnumerateArray()
            .Single(v => v.GetProperty("code").GetString() == "passenger_car");
        Assert.Equal("light_duty", car.GetProperty("parentCode").GetString());
    }
}
