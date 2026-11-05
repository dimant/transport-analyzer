using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using TransportAnalyzer.Api.Query;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;
using TransportAnalyzer.Ingestion;

namespace TransportAnalyzer.Tests;

/// <summary>Resets fars 2022 to the clean fixture once (other FARS tests ingest variants that add crashes),
/// and hosts one API over that state for every aggregate test.</summary>
public sealed class AggregateFixture(PostgresFixture db) : IAsyncLifetime
{
    public ApiFactory Factory { get; } = new(db);
    public string ConnectionString => db.ConnectionString;

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var del = new NpgsqlCommand(
                "delete from event where source_id = 'fars' and source_key like '2022-%'", conn);
            await del.ExecuteNonQueryAsync();
        }
        await new IngestRunner(db.ConnectionString, new RawStore(Factory.RawRoot))
            .RunAsync(FarsTestSource.Adapter(FarsTestSource.BuildZip(2022)), new IngestParameters([2022]), CancellationToken.None);
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

[Collection("db")]
public class AggregateTests(AggregateFixture fx) : IClassFixture<AggregateFixture>
{
    private const string Year2022 = "from=2022-01-01&to=2022-12-31";

    private async Task<Table> GetAsync(string query)
    {
        var response = await fx.Factory.CreateClient().GetAsync("/api/events/aggregate?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new Table(await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private async Task AssertBadRequestAsync(string query, string parameter)
    {
        var response = await fx.Factory.CreateClient().GetAsync("/api/events/aggregate?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(parameter, body.GetProperty("parameter").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
    }

    /// <summary>The response table with rows addressable by column name.</summary>
    private sealed class Table(JsonElement body)
    {
        public string Grain => body.GetProperty("grain").GetString()!;
        public List<string> Columns { get; } =
            body.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToList();
        public List<JsonElement[]> Rows { get; } =
            body.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().ToArray()).ToList();

        public JsonElement Cell(JsonElement[] row, string column) => row[Columns.IndexOf(column)];
        public long Long(JsonElement[] row, string column) => Cell(row, column).GetInt64();

        public JsonElement[] RowWhere(string column, string? value) =>
            Assert.Single(Rows, r => Cell(r, column) is var c &&
                (value is null ? c.ValueKind == JsonValueKind.Null : c.ValueKind == JsonValueKind.String && c.GetString() == value));
    }

    [Fact]
    public async Task Aggregate_EventGrainByYear()
    {
        var t = await GetAsync($"{Year2022}&bucket=year&f.source_id=fars");

        Assert.Equal("event", t.Grain);
        Assert.Equal(["period", "events", "fatalities", "serious_injuries", "minor_injuries", "unattributed_fatalities"],
            t.Columns);
        var row = Assert.Single(t.Rows);
        Assert.Equal("2022", t.Cell(row, "period").GetString());
        Assert.Equal(3, t.Long(row, "events"));
        Assert.Equal(4, t.Long(row, "fatalities"));
    }

    [Fact]
    public async Task Aggregate_VehicleGrainByClass()
    {
        var t = await GetAsync($"{Year2022}&grain=vehicle&groupBy=vehicle_class&f.source_id=fars");

        Assert.Equal("vehicle", t.Grain);
        Assert.Equal(["vehicle_class", "vehicles", "events", "occupant_fatalities", "occupant_serious_injuries",
            "occupant_minor_injuries", "nonoccupant_fatalities", "nonoccupant_serious_injuries", "nonoccupant_minor_injuries"],
            t.Columns);
        var car = t.RowWhere("vehicle_class", "passenger_car");
        Assert.Equal(2, t.Long(car, "vehicles"));
        Assert.Equal(2, t.Long(car, "events"));
        Assert.Equal(2, t.Long(car, "occupant_fatalities"));
        Assert.Equal(1, t.Long(t.RowWhere("vehicle_class", "light_truck"), "nonoccupant_fatalities"));
        Assert.Equal(1, t.Long(t.RowWhere("vehicle_class", "heavy_truck"), "occupant_serious_injuries"));
        Assert.Equal(["heavy_truck", "light_truck", "passenger_car"],
            t.Rows.Select(r => t.Cell(r, "vehicle_class").GetString()));
    }

    [Fact]
    public async Task Aggregate_VehicleGrainEventsCountsDistinctEvents()
    {
        var t = await GetAsync($"{Year2022}&grain=vehicle&f.source_id=fars");

        var row = Assert.Single(t.Rows);
        Assert.Equal(4, t.Long(row, "vehicles"));
        Assert.Equal(3, t.Long(row, "events"));
    }

    [Fact]
    public async Task Aggregate_GroupByParentRollsUpLightDuty()
    {
        var t = await GetAsync($"{Year2022}&grain=vehicle&groupBy=vehicle_class_parent&f.source_id=fars");

        Assert.Equal(3, t.Long(t.RowWhere("vehicle_class_parent", "light_duty"), "vehicles"));
        Assert.Equal(1, t.Long(t.RowWhere("vehicle_class_parent", "heavy_truck"), "vehicles"));
    }

    [Fact]
    public async Task Aggregate_FilterByValue()
    {
        var t = await GetAsync($"{Year2022}&grain=vehicle&f.vehicle_class=heavy_truck&f.source_id=fars");

        Assert.Equal(1, t.Long(Assert.Single(t.Rows), "vehicles"));
    }

    [Fact]
    public async Task Aggregate_FilterByIntegerDimensionAndMultipleValues()
    {
        var t = await GetAsync($"{Year2022}&grain=vehicle&f.year_built=2015,2018&f.source_id=fars");

        Assert.Equal(2, t.Long(Assert.Single(t.Rows), "vehicles"));
    }

    [Fact]
    public async Task Aggregate_NullDimensionValuesFormTheirOwnGroup()
    {
        var t = await GetAsync($"{Year2022}&grain=vehicle&groupBy=year_built&f.source_id=fars");

        Assert.Equal([2015, 2018], t.Rows.Take(2).Select(r => t.Cell(r, "year_built").GetInt32()));
        Assert.Equal(2, t.Long(t.RowWhere("year_built", null), "vehicles"));
    }

    [Fact]
    public async Task Aggregate_GroupColumnsThenPeriodThenMeasures_MonthAndQuarterFormats()
    {
        var month = await GetAsync($"{Year2022}&groupBy=state,mode&bucket=month&f.source_id=fars");
        Assert.Equal(["state", "mode", "period"], month.Columns.Take(3));
        Assert.Equal(["2022-03", "2022-07", "2022-11"],
            month.Rows.OrderBy(r => month.Cell(r, "period").GetString()).Select(r => month.Cell(r, "period").GetString()));

        var quarter = await GetAsync($"{Year2022}&bucket=quarter&f.source_id=fars");
        Assert.Equal(["2022-Q1", "2022-Q3", "2022-Q4"], quarter.Rows.Select(r => quarter.Cell(r, "period").GetString()));
    }

    [Fact]
    public async Task Aggregate_DateRangeIsInclusive()
    {
        var t = await GetAsync("from=2022-03-14&to=2022-07-04&f.source_id=fars");

        Assert.Equal(2, t.Long(Assert.Single(t.Rows), "events"));
    }

    [Fact]
    public async Task Aggregate_UnknownDimension_Returns400()
    {
        await AssertBadRequestAsync($"{Year2022}&grain=vehicle&groupBy=vehicle_class;drop", "groupBy");
        await AssertBadRequestAsync($"{Year2022}&f.nope=1", "f.nope");
    }

    [Fact]
    public async Task Aggregate_VehicleDimensionAtEventGrain_Returns400()
    {
        await AssertBadRequestAsync($"{Year2022}&groupBy=vehicle_class", "groupBy");
        await AssertBadRequestAsync($"{Year2022}&f.vehicle_class=bus", "f.vehicle_class");
    }

    [Theory]
    [InlineData("to=2022-12-31", "from")]
    [InlineData("from=2022-01-01", "to")]
    [InlineData("from=2022-13-01&to=2022-12-31", "from")]
    [InlineData("from=2022-01-01&to=yesterday", "to")]
    [InlineData("from=2022-12-31&to=2022-01-01", "from")]
    [InlineData(Year2022 + "&bucket=week", "bucket")]
    [InlineData(Year2022 + "&grain=person", "grain")]
    [InlineData(Year2022 + "&groupBy=mode,state,mode", "groupBy")]
    [InlineData(Year2022 + "&grain=vehicle&f.occupants=two", "f.occupants")]
    [InlineData(Year2022 + "&f.mode=", "f.mode")]
    public async Task Aggregate_InvalidParameters_Return400(string query, string parameter) =>
        await AssertBadRequestAsync(query, parameter);

    [Fact]
    public async Task Create_BuildsTheSameQueryInCode_AndRunAsyncReadsRows()
    {
        var created = AggregateQuery.Create("vehicle", new DateOnly(2022, 1, 1), new DateOnly(2022, 12, 31), "year",
            ["mode", "vehicle_class", "regulation_class"],
            new Dictionary<string, IReadOnlyList<string>> { ["source_id"] = ["fars"] });
        Assert.True(created.IsSuccess);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var result = await created.Value.RunAsync(conn, CancellationToken.None);

        Assert.Equal("vehicle", result.Grain);
        Assert.Equal(["mode", "vehicle_class", "regulation_class", "period"], result.Columns.Take(4));
        var fatal = result.Columns.ToList().IndexOf("occupant_fatalities");
        Assert.Equal(2L, result.Rows.Where(r => (string?)r[1] == "passenger_car").Sum(r => (long)r[fatal]!));

        var bad = AggregateQuery.Create("event", new DateOnly(2022, 1, 1), new DateOnly(2022, 12, 31), "none",
            ["vehicle_class"], new Dictionary<string, IReadOnlyList<string>>());
        Assert.False(bad.IsSuccess);
        Assert.Equal("groupBy", bad.Error.parameter);
    }
}
