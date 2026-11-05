using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using TransportAnalyzer.Api.Query;
using TransportAnalyzer.Infrastructure;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Ingestion;

namespace TransportAnalyzer.Tests;

/// <summary>Clean fars 2022 (4 vehicles) plus the ntsb 2022 fixture (7 vehicles), nothing else.</summary>
public sealed class SampleFixture(PostgresFixture db) : IAsyncLifetime
{
    public ApiFactory Factory { get; } = new(db);

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var del = new NpgsqlCommand("delete from event where source_id = 'ntsb'", conn);
            await del.ExecuteNonQueryAsync();
        }
        await FarsTestSource.ResetToCleanFixtureAsync(db.ConnectionString, Factory.RawRoot);
        await new IngestRunner(db.ConnectionString, new RawStore(Factory.RawRoot))
            .RunAsync(NtsbTestSource.Adapter(), new IngestParameters([2022]), CancellationToken.None);
    }

    /// <summary>Re-ingests both fixtures unchanged; normalize regenerates every event_id and vehicle_id.</summary>
    public async Task ReingestAsync()
    {
        await FarsTestSource.ResetToCleanFixtureAsync(db.ConnectionString, Factory.RawRoot);
        await new IngestRunner(db.ConnectionString, new RawStore(Factory.RawRoot))
            .RunAsync(NtsbTestSource.Adapter(), new IngestParameters([2022]), CancellationToken.None);
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

[Collection("db")]
public class SampleTests(SampleFixture fx) : IClassFixture<SampleFixture>
{
    private const string Year2022 = "from=2022-01-01&to=2022-12-31";

    private async Task<JsonElement> GetAsync(string query)
    {
        var response = await fx.Factory.CreateClient().GetAsync("/api/events/sample?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<string> Rows(JsonElement body) =>
        body.GetProperty("rows").EnumerateArray().Select(r => r.GetRawText()).ToList();

    private async Task AssertBadRequestAsync(string query, string parameter)
    {
        var response = await fx.Factory.CreateClient().GetAsync("/api/events/sample?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(parameter, body.GetProperty("parameter").GetString());
    }

    [Fact]
    public async Task Sample_SameSeed_SameRows()
    {
        var a = await GetAsync($"{Year2022}&n=5&seed=42");
        var b = await GetAsync($"{Year2022}&n=5&seed=42");

        Assert.Equal(Rows(a), Rows(b));
        Assert.Equal(5, a.GetProperty("n").GetInt32());
        Assert.Equal(42, a.GetProperty("seed").GetInt64());
    }

    [Fact]
    public async Task Sample_SameSeedAcrossReingest_SameRows()
    {
        var before = Rows(await GetAsync($"{Year2022}&seed=42"));
        await fx.ReingestAsync();
        var after = Rows(await GetAsync($"{Year2022}&seed=42"));

        Assert.Equal(11, before.Count);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Sample_DifferentSeed_DifferentOrder()
    {
        var a = Rows(await GetAsync($"{Year2022}&seed=1"));
        var b = Rows(await GetAsync($"{Year2022}&seed=2"));

        Assert.Equal(a.Order().ToList(), b.Order().ToList());
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Sample_RespectsFiltersAndN()
    {
        var body = await GetAsync($"{Year2022}&f.mode=aviation&n=3");

        var columns = body.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(["source_id", "source_key", "vehicle_no", .. Dimensions.All.Keys.Where(k => k != "source_id"), "occurred_on", "occupant_fatalities", "occupant_serious_injuries",
            "occupant_minor_injuries", "nonoccupant_fatalities", "nonoccupant_serious_injuries", "nonoccupant_minor_injuries"],
            columns);
        var rows = body.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, body.GetProperty("n").GetInt32());
        Assert.Equal(7, body.GetProperty("total").GetInt64());
        Assert.All(rows, r => Assert.Equal("aviation", r[columns.IndexOf("mode")].GetString()));
        Assert.All(rows, r => Assert.StartsWith("2022-", r[columns.IndexOf("occurred_on")].GetString()));
        Assert.All(rows, r => Assert.Equal("ntsb", r[columns.IndexOf("source_id")].GetString()));
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r[columns.IndexOf("source_key")].GetString())));
        Assert.All(rows, r => Assert.True(r[columns.IndexOf("vehicle_no")].GetInt32() >= 1));
    }

    [Fact]
    public async Task Sample_NOutOfRange_Returns400()
    {
        await AssertBadRequestAsync($"{Year2022}&n=0", "n");
        await AssertBadRequestAsync($"{Year2022}&n=100001", "n");
        await AssertBadRequestAsync($"{Year2022}&n=abc", "n");
    }

    [Fact]
    public async Task Sample_BadSeed_Returns400() => await AssertBadRequestAsync($"{Year2022}&seed=x", "seed");

    [Fact]
    public async Task Sample_UnknownFilter_Returns400()
    {
        await AssertBadRequestAsync($"{Year2022}&f.bogus=x", "f.bogus");
        await AssertBadRequestAsync($"{Year2022}&f.year_built=abc", "f.year_built");
        await AssertBadRequestAsync("from=2022-01-01", "to");
    }
}
