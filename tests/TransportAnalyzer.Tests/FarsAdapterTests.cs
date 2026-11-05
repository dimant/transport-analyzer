// FARS adapter tests over the hand-built 2022 fixture (Fixtures/fars/2022).
//
// Recon (FARS2019..2023NationalCSV.zip from static.nhtsa.gov, 2026-11-04): the brief's column names are the real
// ones (upper case in the files): accident ST_CASE, STATE, STATENAME, YEAR, MONTH, DAY, HOUR, MINUTE, LATITUDE,
// LONGITUD, FATALS, CITYNAME; vehicle VEH_NO, NUMOCCS, MAKE, MAKENAME, MODEL, MAK_MODNAME, BODY_TYP, MOD_YEAR,
// SPEC_USE; person VEH_NO, PER_NO, STR_VEH, PER_TYP, INJ_SEV. Differences that shape the fixture:
// - there is no MODELNAME column; the model name lives in MAK_MODNAME (make + model), MODEL is a per-make code.
// - 2020+ zips nest lower-case files under FARS{year}NationalCSV/; 2019 has mixed-case names at the root
//   (accident.CSV, Person.CSV, vehicle.csv). The test zip mixes both layouts.
// - the 2022 accident.csv starts with a UTF-8 BOM; so does the fixture's.
// - BODY_TYP 31 (fixture crash 10002) is a pre-2017 pickup code, absent from 2019-2023 data but inside the 30-39
//   pickup range, which fars.csv maps code by code.
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;
using TransportAnalyzer.Ingestion;
using TransportAnalyzer.Ingestion.Fars;

namespace TransportAnalyzer.Tests;

[Collection("db")]
public class FarsAdapterTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly List<string> _rawRoots = [];

    // This class owns the fars 2022 rows; clear them so each test starts from an empty year.
    public async Task InitializeAsync() =>
        await ExecAsync("delete from event where source_id = 'fars' and source_key like '2022-%'");

    public Task DisposeAsync()
    {
        foreach (var root in _rawRoots.Where(Directory.Exists))
            Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<Guid> IngestAsync(byte[]? zip = null, FarsTestSource.StubHandler? handler = null)
    {
        // A fresh raw-store root per run: RawStore never overwrites, and two runs can land in the same second.
        var root = Path.Combine(Path.GetTempPath(), "ta-raw-" + Guid.NewGuid().ToString("N"));
        _rawRoots.Add(root);
        var adapter = FarsTestSource.Adapter(zip ?? FarsTestSource.BuildZip(2022), handler);
        return await new IngestRunner(db.ConnectionString, new RawStore(root))
            .RunAsync(adapter, new IngestParameters([2022]), CancellationToken.None);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Dictionary<string, object?>> RowAsync(string sql)
    {
        var rows = await RowsAsync(sql);
        return Assert.Single(rows);
    }

    private async Task<List<Dictionary<string, object?>>> RowsAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private async Task AssertSucceededAsync(Guid runId)
    {
        var run = await RowAsync($"select status, error from ingest_run where ingest_run_id = '{runId}'");
        Assert.True((string)run["status"]! == "succeeded", $"run failed: {run["error"]}");
    }

    private Task<Dictionary<string, object?>> EventAsync(string key) =>
        RowAsync($"select *, occurred_on::text on_text, occurred_time::text time_text from event where source_id = 'fars' and source_key = '{key}'");

    private Task<Dictionary<string, object?>> VehicleAsync(string key, int vehicleNo) =>
        RowAsync($"""
            select v.* from vehicle v join event e using (event_id)
            where e.source_id = 'fars' and e.source_key = '{key}' and v.vehicle_no = {vehicleNo}
            """);

    [Fact]
    public async Task Normalize_SingleVehicleDriverKilled()
    {
        await AssertSucceededAsync(await IngestAsync());

        var e = await EventAsync("2022-10001");
        Assert.Equal(1, e["fatalities"]);
        Assert.Equal(0, e["serious_injuries"]);
        Assert.Equal(0, e["unattributed_fatalities"]);
        Assert.Equal("highway", e["mode"]);
        Assert.Equal("accident", e["event_type"]);
        Assert.Equal("fatal", e["severity"]);
        Assert.Equal("2022-03-14", e["on_text"]);
        Assert.Equal("08:30:00", e["time_text"]);
        Assert.Equal(32.66622222, (double)e["latitude"]!, 6);
        Assert.Equal(-85.33665833, (double)e["longitude"]!, 6);
        Assert.Equal("Alabama", e["state"]);
        Assert.Equal("US", e["country"]);
        Assert.Equal("1", JsonDocument.Parse((string)e["source_payload"]!).RootElement.GetProperty("fatals").GetString());

        var v = await VehicleAsync("2022-10001", 1);
        Assert.Equal("passenger_car", v["vehicle_class"]);
        Assert.Equal("road_private", v["regulation_class"]);
        Assert.Equal("private", v["operator_type"]);
        Assert.Equal("unknown", v["maintenance_regime"]);
        Assert.Equal(1, v["occupant_fatalities"]);
        Assert.Equal(0, v["nonoccupant_fatalities"]);
        Assert.Equal(2015, v["year_built"]);
        Assert.Equal(7, v["vehicle_age_years"]);
        Assert.Equal(1, v["occupants"]);
        Assert.Equal(1, v["crew_count"]);
        Assert.Equal("Toyota", v["make"]);
        Assert.Equal("Toyota Camry", v["model"]);
        Assert.Equal("4", JsonDocument.Parse((string)v["source_payload"]!).RootElement.GetProperty("body_typ").GetString());
    }

    [Fact]
    public async Task Normalize_PedestrianAttributedToStrikingVehicle()
    {
        await AssertSucceededAsync(await IngestAsync());

        var e = await EventAsync("2022-10002");
        Assert.Equal(1, e["fatalities"]);
        Assert.Equal(0, e["unattributed_fatalities"]);
        Assert.Equal("6", e["state"]); // STATENAME empty in the fixture: falls back to the numeric code

        var v = await VehicleAsync("2022-10002", 1);
        Assert.Equal("light_truck", v["vehicle_class"]);
        Assert.Equal(0, v["occupant_fatalities"]);
        Assert.Equal(1, v["nonoccupant_fatalities"]);
        Assert.Equal(0, v["occupant_minor_injuries"]);
    }

    [Fact]
    public async Task Normalize_UnattributableCasualties_LandOnEvent()
    {
        await AssertSucceededAsync(await IngestAsync());

        var e = await EventAsync("2022-10003");
        Assert.Equal(2, e["fatalities"]);
        Assert.Equal(2, e["serious_injuries"]);
        Assert.Equal(1, e["minor_injuries"]);
        Assert.Equal(1, e["unattributed_fatalities"]);       // cyclist, STR_VEH 0
        Assert.Equal(1, e["unattributed_serious_injuries"]); // occupant of VEH_NO 3, absent from vehicle.csv
        Assert.Equal(0, e["unattributed_minor_injuries"]);
        Assert.Equal("2022-11-20", e["on_text"]);
        Assert.Null(e["occurred_time"]);                     // HOUR 99, MINUTE 99
        Assert.Null(e["latitude"]);                          // 77.7777
        Assert.Null(e["longitude"]);                         // 777.7777

        var v1 = await VehicleAsync("2022-10003", 1);
        Assert.Equal(1, v1["occupant_fatalities"]);
        Assert.Equal(1, v1["occupant_minor_injuries"]);
        Assert.Equal(0, v1["occupant_serious_injuries"]);
        Assert.Null(v1["year_built"]);                       // MOD_YEAR empty
        Assert.Null(v1["vehicle_age_years"]);
        Assert.Equal("37", v1["make"]);                      // MAKENAME empty: falls back to the code
        Assert.Equal("32", v1["model"]);

        var v2 = await VehicleAsync("2022-10003", 2);
        Assert.Equal("heavy_truck", v2["vehicle_class"]);
        Assert.Equal("road_commercial", v2["regulation_class"]);
        Assert.Equal("commercial", v2["operator_type"]);
        Assert.Equal("mandated_periodic", v2["maintenance_regime"]);
        Assert.Equal(1, v2["occupant_serious_injuries"]);
        Assert.Equal(0, v2["occupant_fatalities"]);
        Assert.Null(v2["year_built"]);                       // MOD_YEAR 9999
        Assert.Null(v2["occupants"]);                        // NUMOCCS 99
    }

    [Fact]
    public async Task Normalize_EventInvariantHoldsForAllEvents()
    {
        await AssertSucceededAsync(await IngestAsync());

        var broken = await RowsAsync("""
            select e.source_key from event e
            left join (select event_id,
                              sum(occupant_fatalities + nonoccupant_fatalities) f,
                              sum(occupant_serious_injuries + nonoccupant_serious_injuries) s,
                              sum(occupant_minor_injuries + nonoccupant_minor_injuries) m
                       from vehicle group by event_id) v using (event_id)
            where e.source_id = 'fars'
              and (e.fatalities <> e.unattributed_fatalities + coalesce(v.f, 0)
                or e.serious_injuries <> e.unattributed_serious_injuries + coalesce(v.s, 0)
                or e.minor_injuries <> e.unattributed_minor_injuries + coalesce(v.m, 0))
            """);
        Assert.Empty(broken);
        var total = await RowAsync("select count(*) n, sum(fatalities) f from event where source_id = 'fars' and source_key like '2022-%'");
        Assert.Equal(3L, total["n"]);
        Assert.Equal(4L, total["f"]);
    }

    [Fact]
    public async Task Stage_KeepsUnusedColumns()
    {
        await AssertSucceededAsync(await IngestAsync());

        var staged = await RowAsync("select cityname from stg_fars.accident where data_year = '2022' and st_case = '10001'");
        Assert.Equal("OPELIKA", staged["cityname"]);
        var e = await RowAsync("select source_payload->>'cityname' city from event where source_id = 'fars' and source_key = '2022-10001'");
        Assert.Equal("OPELIKA", e["city"]);
        // The root-level, mixed-case Person.CSV entry lands in stg_fars.person.
        var persons = await RowAsync("select count(*) n from stg_fars.person where data_year = '2022'");
        Assert.Equal(8L, persons["n"]);
    }

    [Fact]
    public async Task Ingest_SameYearTwice_IsIdempotent()
    {
        var handler = new FarsTestSource.StubHandler(FarsTestSource.BuildZip(2022));
        var first = await IngestAsync(handler: handler);
        var second = await IngestAsync(handler: handler);
        await AssertSucceededAsync(first);
        await AssertSucceededAsync(second);
        Assert.Equal("https://static.nhtsa.gov/nhtsa/downloads/FARS/2022/National/FARS2022NationalCSV.zip",
            handler.Requests[0].ToString());

        var counts = await RowAsync("""
            select (select count(*) from event where source_id = 'fars' and source_key like '2022-%') events,
                   (select count(*) from vehicle v join event e using (event_id)
                    where e.source_id = 'fars' and e.source_key like '2022-%') vehicles,
                   (select count(*) from event where source_id = 'fars' and source_key like '2022-%'
                    and ingest_run_id <> @run) stale_events,
                   (select count(*) from vehicle v join event e using (event_id)
                    where e.source_id = 'fars' and e.source_key like '2022-%' and v.ingest_run_id <> @run) stale_vehicles
            """.Replace("@run", $"'{second}'"));
        Assert.Equal(3L, counts["events"]);
        Assert.Equal(4L, counts["vehicles"]);
        Assert.Equal(0L, counts["stale_events"]);
        Assert.Equal(0L, counts["stale_vehicles"]);

        var run = await RowAsync($"select row_counts::text rc, unmapped::text um from ingest_run where ingest_run_id = '{second}'");
        var rc = JsonDocument.Parse((string)run["rc"]!).RootElement;
        Assert.Equal(3, rc.GetProperty("event").GetInt64());
        Assert.Equal(4, rc.GetProperty("vehicle").GetInt64());
        Assert.Equal("{}", run["um"]);
    }

    [Fact]
    public async Task Normalize_UnmappedValues_BecomeUnknownAndAreCounted()
    {
        // SPEC_USE 77 is not a FARS code; crash 10002's light truck has no body type rule for regulation or operator.
        var zip = FarsTestSource.BuildZip(2022, (file, text) =>
            file == "vehicle.csv" ? text.Replace("31,2018,0", "31,2018,77") : text);
        var runId = await IngestAsync(zip);
        await AssertSucceededAsync(runId);

        var v = await VehicleAsync("2022-10002", 1);
        Assert.Equal("unknown", v["regulation_class"]);
        Assert.Equal("unknown", v["operator_type"]);
        Assert.Equal("light_truck", v["vehicle_class"]);

        var run = await RowAsync($"select unmapped::text um from ingest_run where ingest_run_id = '{runId}'");
        var unmapped = JsonSerializer.Deserialize<Dictionary<string, long>>((string)run["um"]!)!;
        Assert.Equal(new Dictionary<string, long>
        {
            ["operator_type:spec_use=77"] = 1,
            ["regulation_class:spec_use=77"] = 1,
        }, unmapped);
    }

    [Fact]
    public async Task Run_RecordsUnknownCountsPerAttribute()
    {
        var runId = await IngestAsync();
        await AssertSucceededAsync(runId);

        var run = await RowAsync($"select row_counts::text rc from ingest_run where ingest_run_id = '{runId}'");
        var rc = JsonSerializer.Deserialize<Dictionary<string, long>>((string)run["rc"]!)!;
        // Only the heavy truck (2022-10003 vehicle 2) has a maintenance regime; the two cars and the pickup do not.
        Assert.Equal(3, rc["unknown:maintenance_regime"]);
        Assert.Equal(0, rc["unknown:vehicle_class"]);
        Assert.Equal(4, rc["vehicle"]);
        var actual = await RowAsync($"""
            select count(*) filter (where operator_type = 'unknown') ot,
                   count(*) filter (where regulation_class = 'unknown') rc
            from vehicle where ingest_run_id = '{runId}'
            """);
        Assert.Equal((long)actual["ot"]!, rc["unknown:operator_type"]);
        Assert.Equal((long)actual["rc"]!, rc["unknown:regulation_class"]);
    }

    [Fact]
    public async Task Mappings_AllCodesExistInVocabulary()
    {
        var missing = await RowsAsync("""
            select m.attribute, m.source_column, m.source_value, m.code from vocabulary_map m
            where m.source_id = 'fars'
              and not exists (select 1 from vocabulary v where v.attribute = m.attribute and v.code = m.code)
            """);
        Assert.Empty(missing);
        var attributes = await RowsAsync("select distinct attribute from vocabulary_map where source_id = 'fars' order by 1");
        Assert.Equal(new[] { "maintenance_regime", "operator_type", "regulation_class", "vehicle_class" },
            attributes.Select(a => (string)a["attribute"]!));
    }

    [Fact]
    public async Task Host_RegistersFarsAdapter()
    {
        await using var factory = new ApiFactory(db);
        var adapters = factory.Services.GetServices<ISourceAdapter>().ToList();
        var fars = Assert.Single(adapters, a => a.Descriptor.SourceId == "fars");
        Assert.IsType<FarsAdapter>(fars);
        Assert.Equal("highway", fars.Descriptor.Mode);
        Assert.Equal("events", fars.Descriptor.Kind);
    }
}
