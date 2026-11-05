// NTSB adapter tests over the hand-built 2022 fixture (Fixtures/ntsb), served by FakeMdbExporter.
//
// Fixture shape follows the recon (docs/research/2026-11-04-ntsb-faa-recon.md §1): mdb-export default dialect (text
// quoted, numbers bare, NULL = empty field, dates "MM/DD/YY 00:00:00"), table names as in the MDB, column Aircraft_Key
// capitalised, injury categories truncated to 4 chars (Flig/Pass/Cabi), far_part zero-padded ("091"), type_fly with a
// trailing-space variant ("INST "). Extra cases beyond the brief: E3 aircraft 1 narrative is "import" (falls back to
// narr_accp), E4 lists engine 2 before engine 1 and has acft_year 0, E5 latitude 91.5 and E6 longitude -200 are out of
// range, E5 make/model carry outer spaces.
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;
using TransportAnalyzer.Ingestion;
using TransportAnalyzer.Ingestion.Ntsb;

namespace TransportAnalyzer.Tests;

[Collection("db")]
public class NtsbAdapterTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly List<string> _rawRoots = [];

    public async Task InitializeAsync() => await ExecAsync("delete from event where source_id = 'ntsb'");

    public Task DisposeAsync()
    {
        foreach (var root in _rawRoots.Where(Directory.Exists))
            Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<Guid> IngestAsync(FakeMdbExporter? mdb = null, IReadOnlyList<int>? years = null,
        FarsTestSource.StubHandler? handler = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ta-raw-" + Guid.NewGuid().ToString("N"));
        _rawRoots.Add(root);
        return await new IngestRunner(db.ConnectionString, new RawStore(root))
            .RunAsync(NtsbTestSource.Adapter(mdb, handler), new IngestParameters(years), CancellationToken.None);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Dictionary<string, object?>> RowAsync(string sql) => Assert.Single(await RowsAsync(sql));

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

    private async Task<Dictionary<string, long>> RunJsonAsync(Guid runId, string column)
    {
        var run = await RowAsync($"select {column}::text j from ingest_run where ingest_run_id = '{runId}'");
        return JsonSerializer.Deserialize<Dictionary<string, long>>((string)run["j"]!)!;
    }

    private Task<Dictionary<string, object?>> EventAsync(string key) =>
        RowAsync($"select *, occurred_on::text on_text from event where source_id = 'ntsb' and source_key = '{key}'");

    private Task<Dictionary<string, object?>> AircraftAsync(string key, int aircraftKey) =>
        RowAsync($"""
            select v.* from vehicle v join event e using (event_id)
            where e.source_id = 'ntsb' and e.source_key = '{key}' and v.vehicle_no = {aircraftKey}
            """);

    [Fact]
    public async Task Normalize_SingleAircraft_CountsOccupantsAndIgnoresTotl()
    {
        await AssertSucceededAsync(await IngestAsync());

        var e = await EventAsync("20220101X00001");
        Assert.Equal("aviation", e["mode"]);
        Assert.Equal("accident", e["event_type"]);
        Assert.Equal("2022-01-15", e["on_text"]);
        Assert.Null(e["occurred_time"]);
        Assert.Equal("US", e["country"]);
        Assert.Equal("CA", e["state"]);
        Assert.Equal("fatal", e["severity"]);
        Assert.Equal(36.776, (double)e["latitude"]!, 6);
        Assert.Equal(-119.718, (double)e["longitude"]!, 6);
        Assert.Equal(1, e["fatalities"]);
        Assert.Equal(1, e["serious_injuries"]);
        Assert.Equal(0, e["minor_injuries"]);
        Assert.Equal(0, e["unattributed_fatalities"]);
        Assert.Equal(0, e["ground_fatalities"]); // inj_f_grnd blank counts as 0
        Assert.Equal("part_91", e["operation_class"]);
        Assert.Equal("Loss of control.", e["probable_cause"]);
        var payload = JsonDocument.Parse((string)e["source_payload"]!).RootElement;
        Assert.Equal("WPR22FA001", payload.GetProperty("ntsb_no").GetString());
        Assert.Equal("1430", payload.GetProperty("ev_time").GetString());

        var v = await AircraftAsync("20220101X00001", 1);
        Assert.Equal(1, v["occupant_fatalities"]);
        Assert.Equal(1, v["occupant_serious_injuries"]);
        Assert.Equal(0, v["occupant_minor_injuries"]);
        Assert.Equal(0, v["nonoccupant_fatalities"]);
        Assert.Equal(3, v["occupants"]);   // FATL 1 + SERS 1 + NONE 1; TOTL 9 ignored
        Assert.Equal(1, v["crew_count"]);  // Flig FATL 1
        Assert.Equal("light_aircraft", v["vehicle_class"]);
        Assert.Equal("part_91", v["regulation_class"]);
        Assert.Equal("private", v["operator_type"]);
        Assert.Equal("mandated_periodic", v["maintenance_regime"]);
        Assert.Equal(1978, v["year_built"]);
        Assert.Equal(44, v["vehicle_age_years"]);
        Assert.Equal("N1234A", v["registration"]);
        Assert.Equal("PIPER", v["make"]);
        Assert.Equal("PA-28-180", v["model"]);
        Assert.Equal("DEST", v["damage"]);
        Assert.Equal("WPR22FA001", JsonDocument.Parse((string)v["source_payload"]!).RootElement.GetProperty("ntsb_no").GetString());
    }

    [Fact]
    public async Task Normalize_Incident_IsIncidentWithNoCasualties()
    {
        await AssertSucceededAsync(await IngestAsync());

        var e = await EventAsync("20220202X00002");
        Assert.Equal("incident", e["event_type"]);
        Assert.Equal(0, e["fatalities"]);
        Assert.Equal(0, e["serious_injuries"]);
        Assert.Equal(0, e["minor_injuries"]);
        Assert.Equal("none", e["severity"]);
        Assert.Null(e["latitude"]);
        Assert.Null(e["longitude"]);
        Assert.Null(e["narrative"]); // no narrative row

        var v = await AircraftAsync("20220202X00002", 1);
        Assert.Equal("airliner", v["vehicle_class"]);
        Assert.Equal("part_121", v["regulation_class"]);
        Assert.Equal("commercial", v["operator_type"]);
        Assert.Equal(125, v["occupants"]);
        Assert.Equal(5, v["crew_count"]);
        Assert.Equal(0, v["occupant_fatalities"]);
    }

    [Fact]
    public async Task Normalize_MidairGroundFatality_IsUnattributed()
    {
        await AssertSucceededAsync(await IngestAsync());

        var e = await EventAsync("20220303X00003");
        Assert.Equal(3, e["fatalities"]);
        Assert.Equal(2, e["minor_injuries"]);
        Assert.Equal(1, e["unattributed_fatalities"]);
        Assert.Equal(1, e["ground_fatalities"]);
        Assert.Equal("Midair over Ocala.", e["narrative"]); // aircraft 1 narr_accf is "import"
        Assert.Null(e["probable_cause"]);                   // aircraft 1 narr_cause blank
        Assert.Equal("part_91", e["operation_class"]);

        var ac1 = await AircraftAsync("20220303X00003", 1);
        Assert.Equal("experimental", ac1["vehicle_class"]);
        Assert.Equal("commercial", ac1["operator_type"]); // 091:INST (type_fly "INST " trimmed)
        Assert.Equal(1, ac1["occupant_fatalities"]);
        Assert.Equal(0, ac1["nonoccupant_fatalities"]);

        var ac2 = await AircraftAsync("20220303X00003", 2);
        Assert.Equal("turboprop", ac2["vehicle_class"]);
        Assert.Equal("part_135", ac2["regulation_class"]);
        Assert.Equal("commercial", ac2["operator_type"]);
        Assert.Equal(1, ac2["occupant_fatalities"]);
        Assert.Equal(2, ac2["occupant_minor_injuries"]);
        Assert.Equal(0, ac2["nonoccupant_fatalities"]);
    }

    [Fact]
    public async Task Normalize_SingleAircraftGroundFatality_IsNonoccupant()
    {
        await AssertSucceededAsync(await IngestAsync());

        var e = await EventAsync("20220404X00004");
        Assert.Equal("BR", e["country"]);
        Assert.Null(e["state"]);
        Assert.Equal(4, e["fatalities"]);
        Assert.Equal(0, e["unattributed_fatalities"]);
        Assert.Equal(2, e["ground_fatalities"]);
        Assert.Equal("other", e["operation_class"]);

        var v = await AircraftAsync("20220404X00004", 1);
        Assert.Equal(2, v["nonoccupant_fatalities"]);
        Assert.Equal(2, v["occupant_fatalities"]);
        Assert.Equal("other", v["regulation_class"]);
        Assert.Equal("business_jet", v["vehicle_class"]); // engine 1 TF, not engine 2 UNK
        Assert.Equal("unknown", v["maintenance_regime"]);
        Assert.Null(v["year_built"]);                     // acft_year 0
        Assert.Null(v["vehicle_age_years"]);
    }

    [Fact]
    public async Task Normalize_Part135Scheduled_HasOwnRegulationClass()
    {
        await AssertSucceededAsync(await IngestAsync());

        var e = await EventAsync("20220505X00005");
        Assert.Null(e["latitude"]); // 91.5
        Assert.Equal(-134.42, (double)e["longitude"]!, 6);

        var v = await AircraftAsync("20220505X00005", 1);
        Assert.Equal("part_135_scheduled", v["regulation_class"]);
        Assert.Equal("turboprop", v["vehicle_class"]);
        Assert.Equal("DE HAVILLAND", v["make"]);
        Assert.Equal("DHC-2", v["model"]);
        Assert.Null(v["crew_count"]); // only a Pass row
        Assert.Equal(1, v["occupants"]);
    }

    [Fact]
    public async Task Normalize_BlankFarPartAndUnmappedUse_AreUnknown()
    {
        var runId = await IngestAsync();
        await AssertSucceededAsync(runId);

        var e = await EventAsync("20220606X00006");
        Assert.Equal("serious", e["severity"]);
        Assert.Null(e["longitude"]); // -200
        Assert.Equal("unknown", e["operation_class"]);

        var v = await AircraftAsync("20220606X00006", 1);
        Assert.Equal("helicopter", v["vehicle_class"]);
        Assert.Equal("unknown", v["regulation_class"]);
        Assert.Equal("unknown", v["operator_type"]);
        Assert.Equal("unknown", v["maintenance_regime"]);

        // regulation_class has the explicit ':%' rule, maintenance_regime the explicit '%' rule: neither is unmapped.
        Assert.Equal(new Dictionary<string, long> { ["operator_type:use_profile=:ZZZZ"] = 1 },
            await RunJsonAsync(runId, "unmapped"));
        var rc = await RunJsonAsync(runId, "row_counts");
        Assert.True(rc["unknown:regulation_class"] >= 1);
        Assert.Equal(6, rc["event"]);
        Assert.Equal(7, rc["vehicle"]);
    }

    [Fact]
    public async Task Stage_MultilineNarrative_IsOneRow()
    {
        await AssertSucceededAsync(await IngestAsync());

        var staged = await RowAsync(
            "select narr_accf from stg_ntsb.narratives where snapshot = 'avall' and ev_id = '20220101X00001'");
        var accf = (string)staged["narr_accf"]!;
        Assert.Contains("\r\n", accf);
        Assert.Contains("\"the engine quit\"", accf);
        Assert.DoesNotContain("\"\"", accf);
        Assert.Equal(accf, (await EventAsync("20220101X00001"))["narrative"]);
        Assert.Equal(3L, (await RowAsync("select count(*) n from stg_ntsb.narratives where snapshot = 'avall'"))["n"]);
    }

    [Fact]
    public async Task Normalize_EventInvariantHolds()
    {
        await AssertSucceededAsync(await IngestAsync());

        var broken = await RowsAsync("""
            select e.source_key from event e
            left join (select event_id,
                              sum(occupant_fatalities + nonoccupant_fatalities) f,
                              sum(occupant_serious_injuries + nonoccupant_serious_injuries) s,
                              sum(occupant_minor_injuries + nonoccupant_minor_injuries) m
                       from vehicle group by event_id) v using (event_id)
            where e.source_id = 'ntsb'
              and (e.fatalities <> e.unattributed_fatalities + coalesce(v.f, 0)
                or e.serious_injuries <> e.unattributed_serious_injuries + coalesce(v.s, 0)
                or e.minor_injuries <> e.unattributed_minor_injuries + coalesce(v.m, 0))
            """);
        Assert.Empty(broken);
        var total = await RowAsync("select count(*) n, sum(fatalities) f from event where source_id = 'ntsb'");
        Assert.Equal(6L, total["n"]);
        Assert.Equal(9L, total["f"]); // 1 + 0 + 3 + 4 + 1 + 0
    }

    [Fact]
    public async Task Ingest_Twice_IsIdempotent()
    {
        var mdb = new FakeMdbExporter();
        var handler = new FarsTestSource.StubHandler(NtsbTestSource.BuildZip());
        var first = await IngestAsync(mdb, handler: handler);
        var second = await IngestAsync(mdb, handler: handler);
        await AssertSucceededAsync(first);
        await AssertSucceededAsync(second);
        Assert.Equal(new NtsbOptions().Url, handler.Requests[0].OriginalString);

        var counts = await RowAsync($"""
            select (select count(*) from event where source_id = 'ntsb') events,
                   (select count(*) from vehicle v join event e using (event_id) where e.source_id = 'ntsb') vehicles,
                   (select count(*) from event where source_id = 'ntsb' and ingest_run_id <> '{second}') stale_events,
                   (select count(*) from vehicle v join event e using (event_id)
                    where e.source_id = 'ntsb' and v.ingest_run_id <> '{second}') stale_vehicles,
                   (select count(*) from stg_ntsb.events where snapshot = 'avall') staged
            """);
        Assert.Equal(6L, counts["events"]);
        Assert.Equal(7L, counts["vehicles"]);
        Assert.Equal(0L, counts["stale_events"]);
        Assert.Equal(0L, counts["stale_vehicles"]);
        Assert.Equal(6L, counts["staged"]);

        // The MDB was extracted to a temp directory that existed during the calls and is gone afterwards; the
        // system table was listed but not exported.
        Assert.NotEmpty(mdb.Calls);
        Assert.All(mdb.Calls, c => Assert.True(c.Existed, $"{c.Path} missing during the call"));
        Assert.All(mdb.Calls, c => Assert.False(File.Exists(c.Path), $"{c.Path} not deleted"));
        Assert.DoesNotContain(FakeMdbExporter.SystemTable, mdb.Exported);
        Assert.Equal(["aircraft", "engines", "events", "injury", "narratives"], mdb.Exported.Take(5).Order());
    }

    [Fact]
    public async Task Ingest_EventDroppedFromSnapshot_IsDeleted()
    {
        await AssertSucceededAsync(await IngestAsync());
        Assert.Equal(6, (await RowsAsync("select 1 from event where source_id = 'ntsb'")).Count);

        var withoutE6 = new FakeMdbExporter((_, text) =>
            string.Join("\n", text.Split('\n').Where(l => !l.Contains("20220606X00006"))));
        await AssertSucceededAsync(await IngestAsync(withoutE6));

        var keys = await RowsAsync("select source_key from event where source_id = 'ntsb' order by 1");
        Assert.Equal(["20220101X00001", "20220202X00002", "20220303X00003", "20220404X00004", "20220505X00005"],
            keys.Select(k => (string)k["source_key"]!));
        var orphans = await RowAsync("select count(*) n from vehicle v where not exists (select 1 from event e where e.event_id = v.event_id)");
        Assert.Equal(0L, orphans["n"]);
    }

    [Fact]
    public async Task Ingest_RequestedYearWithNoEvents_FailsRunAndKeepsEvents()
    {
        await AssertSucceededAsync(await IngestAsync());

        var runId = await IngestAsync(years: [2021, 2022]);
        var run = await RowAsync($"select status, error from ingest_run where ingest_run_id = '{runId}'");
        Assert.Equal("failed", run["status"]);
        Assert.Contains("2021", (string)run["error"]!);
        Assert.Equal(6, (await RowsAsync("select 1 from event where source_id = 'ntsb'")).Count);
    }

    [Fact]
    public async Task Mappings_AllCodesExistInVocabulary()
    {
        var missing = await RowsAsync("""
            select m.attribute, m.source_column, m.source_value, m.code from vocabulary_map m
            where m.source_id = 'ntsb'
              and not exists (select 1 from vocabulary v where v.attribute = m.attribute and v.code = m.code)
            """);
        Assert.Empty(missing);
        var attributes = await RowsAsync("select distinct attribute from vocabulary_map where source_id = 'ntsb' order by 1");
        Assert.Equal(new[] { "maintenance_regime", "operator_type", "regulation_class", "vehicle_class" },
            attributes.Select(a => (string)a["attribute"]!));
    }

    [Fact]
    public async Task Host_RegistersNtsbAdapter()
    {
        await using var factory = new ApiFactory(db);
        var ntsb = Assert.Single(factory.Services.GetServices<ISourceAdapter>(), a => a.Descriptor.SourceId == "ntsb");
        Assert.IsType<NtsbAdapter>(ntsb);
        Assert.Equal("aviation", ntsb.Descriptor.Mode);
        Assert.Equal("events", ntsb.Descriptor.Kind);
        Assert.IsType<MdbToolsExporter>(factory.Services.GetRequiredService<IMdbExporter>());
    }
}
