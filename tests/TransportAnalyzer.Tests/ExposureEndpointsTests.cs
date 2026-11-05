using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace TransportAnalyzer.Tests;

/// <summary>Clean fars 2022 fixture plus hand-seeded 2022 passenger-miles: under the default source
/// <c>bts-passenger-miles</c> (light_duty and the highway total) and a decoy under another source.</summary>
public sealed class ExposureFixture(PostgresFixture db) : IAsyncLifetime
{
    public const string OtherSource = "test-other-exposure";

    public ApiFactory Factory { get; } = new(db);

    public async Task InitializeAsync()
    {
        await FarsTestSource.ResetToCleanFixtureAsync(db.ConnectionString, Factory.RawRoot);
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            delete from exposure where source_id in ('bts-passenger-miles', '{OtherSource}') and year = 2022;
            insert into exposure (exposure_id, source_id, mode, year, vehicle_class, regulation_class, metric, value, provenance, ingest_run_id)
            values (gen_random_uuid(), 'bts-passenger-miles', 'highway', 2022, 'light_duty', null, 'passenger_miles', 1000, 'survey_estimate', gen_random_uuid()),
                   (gen_random_uuid(), 'bts-passenger-miles', 'highway', 2022, null, null, 'passenger_miles', 5000, 'survey_estimate', gen_random_uuid()),
                   (gen_random_uuid(), '{OtherSource}', 'highway', 2022, 'light_duty', null, 'passenger_miles', 7, 'measured', gen_random_uuid());
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

[Collection("db")]
public class ExposureEndpointsTests(ExposureFixture fx) : IClassFixture<ExposureFixture>
{
    private const string Rates2022 = "/api/rates?measure=occupant_fatalities&metric=passenger_miles&fromYear=2022&toYear=2022";

    private async Task<JsonElement> GetAsync(string url)
    {
        var response = await fx.Factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task AssertBadRequestAsync(string url, string parameter)
    {
        var response = await fx.Factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(parameter, body.GetProperty("parameter").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
    }

    private static List<string> Columns(JsonElement body) =>
        body.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToList();

    private static List<Dictionary<string, JsonElement>> Rows(JsonElement body)
    {
        var columns = Columns(body);
        return body.GetProperty("rows").EnumerateArray()
            .Select(r => r.EnumerateArray().Select((cell, i) => (columns[i], cell)).ToDictionary(x => x.Item1, x => x.cell))
            .ToList();
    }

    [Fact]
    public async Task Rates_ByVehicleClass_ReturnsLightDutyRow()
    {
        var body = await GetAsync(Rates2022 + "&groupBy=vehicle_class");

        Assert.Equal("occupant_fatalities", body.GetProperty("measure").GetString());
        Assert.Equal("passenger_miles", body.GetProperty("metric").GetString());
        Assert.Equal(["mode", "year", "vehicle_class", "regulation_class", "numerator", "denominator", "join_level",
            "exposure_source", "provenance"], Columns(body));
        var row = Assert.Single(Rows(body), r => r["vehicle_class"].GetString() == "light_duty");
        Assert.Equal("highway", row["mode"].GetString());
        Assert.Equal(2022, row["year"].GetInt32());
        Assert.Equal(JsonValueKind.Null, row["regulation_class"].ValueKind);
        Assert.Equal(2, row["numerator"].GetInt64());
        Assert.Equal(1000, row["denominator"].GetDouble());
        Assert.Equal("vehicle_class_parent", row["join_level"].GetString());
        Assert.Equal("bts-passenger-miles", row["exposure_source"].GetString());
        Assert.Equal("survey_estimate", row["provenance"].GetString());

        // heavy_truck has no exposure of its own and must not fall back to the highway total.
        Assert.DoesNotContain(Rows(body), r => r["vehicle_class"].ValueKind == JsonValueKind.Null);
        var heavy = Assert.Single(body.GetProperty("unmatched").EnumerateArray(),
            u => u.GetProperty("vehicleClass").GetString() == "heavy_truck");
        Assert.Equal("highway", heavy.GetProperty("mode").GetString());
        Assert.Equal(2022, heavy.GetProperty("year").GetInt32());
        Assert.Equal(JsonValueKind.Null, heavy.GetProperty("regulationClass").ValueKind);
        Assert.Equal(0, heavy.GetProperty("numerator").GetInt64());
        Assert.Equal("no_exposure", heavy.GetProperty("reason").GetString());
        Assert.Equal(["mode", "year", "vehicleClass", "regulationClass", "numerator", "reason"],
            heavy.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Rates_SocietalFatalities_AddsNonoccupantsAndReportsUnattributed()
    {
        var body = await GetAsync(Rates2022.Replace("occupant_fatalities", "societal_fatalities") + "&groupBy=vehicle_class");

        var row = Assert.Single(Rows(body), r => r["vehicle_class"].GetString() == "light_duty");
        Assert.Equal(3, row["numerator"].GetInt64());
        var unattributed = Assert.Single(body.GetProperty("unattributed").EnumerateArray());
        Assert.Equal("highway", unattributed.GetProperty("mode").GetString());
        Assert.Equal(2022, unattributed.GetProperty("year").GetInt32());
        Assert.Equal(1, unattributed.GetProperty("fatalities").GetInt64());
    }

    [Fact]
    public async Task Rates_NotGrouped_UsesModeTotalOfDefaultSource()
    {
        var body = await GetAsync(Rates2022);

        var row = Assert.Single(Rows(body));
        Assert.Equal(JsonValueKind.Null, row["vehicle_class"].ValueKind);
        Assert.Equal(2, row["numerator"].GetInt64());
        Assert.Equal(5000, row["denominator"].GetDouble());
        Assert.Equal("mode", row["join_level"].GetString());
        Assert.Empty(body.GetProperty("unmatched").EnumerateArray());
        // The measure does not change the unattributed list.
        Assert.Single(body.GetProperty("unattributed").EnumerateArray());
    }

    [Fact]
    public async Task Rates_ExposureSource_SelectsOnlyThatSourcesRows()
    {
        // The decoy source is not a registered exposure adapter, so it cannot be chosen...
        await AssertBadRequestAsync(Rates2022 + "&groupBy=vehicle_class&exposureSource=" + ExposureFixture.OtherSource,
            "exposureSource");
        // ...and an events source is not an exposure source.
        await AssertBadRequestAsync(Rates2022 + "&exposureSource=fars", "exposureSource");

        var body = await GetAsync(Rates2022 + "&groupBy=vehicle_class&exposureSource=bts-passenger-miles");
        Assert.Equal(1000, Assert.Single(Rows(body))["denominator"].GetDouble());
    }

    [Fact]
    public async Task Rates_MissingMetric_Returns400() =>
        await AssertBadRequestAsync("/api/rates?measure=occupant_fatalities&fromYear=2022&toYear=2022", "metric");

    [Theory]
    [InlineData("metric=passenger_miles&fromYear=2022&toYear=2022", "measure")]
    [InlineData("measure=fatalities&metric=passenger_miles&fromYear=2022&toYear=2022", "measure")]
    [InlineData("measure=occupant_fatalities&metric=furlongs&fromYear=2022&toYear=2022", "metric")]
    [InlineData("measure=occupant_fatalities&metric=passenger_miles&toYear=2022", "fromYear")]
    [InlineData("measure=occupant_fatalities&metric=passenger_miles&fromYear=2022.5&toYear=2022", "fromYear")]
    [InlineData("measure=occupant_fatalities&metric=passenger_miles&fromYear=2022", "toYear")]
    [InlineData("measure=occupant_fatalities&metric=passenger_miles&fromYear=2022&toYear=x", "toYear")]
    [InlineData("measure=occupant_fatalities&metric=passenger_miles&fromYear=2023&toYear=2022", "fromYear")]
    [InlineData("measure=occupant_fatalities&metric=passenger_miles&fromYear=2022&toYear=2022&groupBy=mode", "groupBy")]
    [InlineData("measure=occupant_fatalities&metric=passenger_miles&fromYear=2022&toYear=2022&groupBy=vehicle_class,vehicle_class", "groupBy")]
    [InlineData("measure=occupant_fatalities&metric=flight_hours&fromYear=2022&toYear=2022", "exposureSource")]
    public async Task Rates_InvalidParameters_Return400(string query, string parameter) =>
        await AssertBadRequestAsync("/api/rates?" + query, parameter);

    [Fact]
    public async Task Exposure_ReturnsRowsForMetric()
    {
        var body = await GetAsync("/api/exposure?metric=passenger_miles&fromYear=2022&toYear=2022&mode=highway&vehicleClass=light_duty");

        Assert.Equal(["source_id", "mode", "year", "vehicle_class", "regulation_class", "metric", "value", "provenance",
            "standard_error"], Columns(body));
        var rows = Rows(body);
        Assert.Equal(["bts-passenger-miles", ExposureFixture.OtherSource], rows.Select(r => r["source_id"].GetString()));
        var bts = rows[0];
        Assert.Equal(2022, bts["year"].GetInt32());
        Assert.Equal(1000, bts["value"].GetDouble());
        Assert.Equal(JsonValueKind.Null, bts["standard_error"].ValueKind);

        var filtered = await GetAsync("/api/exposure?metric=passenger_miles&fromYear=2022&toYear=2022&sourceId=bts-passenger-miles");
        Assert.Equal(2, Rows(filtered).Count);
    }

    [Theory]
    [InlineData("fromYear=2022", "metric")]
    [InlineData("metric=passenger_miles&fromYear=soon", "fromYear")]
    [InlineData("metric=passenger_miles&toYear=soon", "toYear")]
    public async Task Exposure_InvalidParameters_Return400(string query, string parameter) =>
        await AssertBadRequestAsync("/api/exposure?" + query, parameter);
}
