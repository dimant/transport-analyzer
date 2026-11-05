// BTS NTS adapter tests over in-memory ClosedXML workbooks that follow the brief's layout.
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;
using TransportAnalyzer.Ingestion;
using TransportAnalyzer.Ingestion.Bts;

namespace TransportAnalyzer.Tests;

[Collection("db")]
public class BtsNtsTableAdapterTests(PostgresFixture db) : IDisposable
{
    private const string SourceId = "bts-test-pm";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ta-bts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
    }

    private static readonly RowMap[] Map =
    [
        new("Air carrier, domestic, all services", "aviation", null, "part_121", "measured"),
        new("Light duty vehicle, short wheel base", "highway", "light_duty", null, "survey_estimate"),
        new("Light duty vehicle, long wheel base", "highway", "light_duty", null, "survey_estimate"),
        new("Motorcycle", "highway", "motorcycle", null, "survey_estimate"),
    ];

    private static XLWorkbook Workbook(Action<IXLWorksheet>? extra = null)
    {
        var wb = new XLWorkbook();
        // Like the real download: an older-edition sheet comes first and must not be read.
        var decoy = wb.AddWorksheet("1-40_all");
        decoy.Cell(1, 1).Value = "Table 1-40 (older edition)";
        for (var i = 0; i < 5; i++) decoy.Cell(2, i + 2).Value = 2015 + i;
        Row(decoy, 3, "Air carrier, domestic, all services", 7, 7, 7, 7, 7);
        var ws = wb.AddWorksheet("1-40");
        ws.Cell(1, 1).Value = "Table 1-40 U.S. Passenger-Miles (millions)";
        ws.Cell(2, 1).Value = "Source: BTS";
        ws.Cell(4, 2).Value = 2019; ws.Cell(4, 3).Value = 2020; ws.Cell(4, 4).Value = 2021;
        ws.Cell(4, 5).Value = 2022; ws.Cell(4, 6).Value = 2023;
        Row(ws, 5, "Air carrier, domestic, all services", 1, 2, "U", 4, 5);
        Row(ws, 6, "Light duty vehicle, short wheel basea,b", 10, 11, 12, 13, 14);
        Row(ws, 7, "Light duty vehicle, long wheel baseb", 5, 5, 5, 5, 5);
        Row(ws, 8, "Unwanted row", 9, 9, 9, 9, 9);
        Row(ws, 10, "Motorcycleb", 2, 2, 2, 2, 2);
        extra?.Invoke(ws);
        return wb;
    }

    private static void Row(IXLWorksheet ws, int r, string label, params object[] cells)
    {
        ws.Cell(r, 1).Value = label;
        for (var i = 0; i < cells.Length; i++)
            if (cells[i] is string s) ws.Cell(r, i + 2).Value = s;
            else ws.Cell(r, i + 2).Value = Convert.ToDouble(cells[i]);
    }

    private static List<ExposureRow> Parse(XLWorkbook wb) =>
        BtsNtsTableAdapter.Parse(wb, Map, "passenger_miles", 1_000_000, "1-40").ToList();

    [Fact]
    public void Parse_FindsHeaderAndMultipliesUnits()
    {
        var air = Assert.Single(Parse(Workbook()), r => r.Mode == "aviation" && r.Year == 2019);
        Assert.Equal(1_000_000, air.Value);
        Assert.Equal("part_121", air.RegulationClass);
        Assert.Null(air.VehicleClass);
        Assert.Equal("measured", air.Provenance);
        Assert.Equal("passenger_miles", air.Metric);
    }

    [Fact]
    public void Parse_SkipsNonNumericCells()
    {
        var rows = Parse(Workbook());
        Assert.DoesNotContain(rows, r => r.Mode == "aviation" && r.Year == 2021);
        Assert.Contains(rows, r => r.Mode == "aviation" && r.Year == 2022);
    }

    [Fact]
    public void Parse_SumsRowsMappedToSameKey()
    {
        var ld = Assert.Single(Parse(Workbook()), r => r.VehicleClass == "light_duty" && r.Year == 2020);
        Assert.Equal(16_000_000, ld.Value);
        Assert.Equal("highway", ld.Mode);
        Assert.Null(ld.RegulationClass);
    }

    [Fact]
    public void Parse_IgnoresUnmappedLabels()
    {
        Assert.DoesNotContain(Parse(Workbook()), r => r.Value == 9_000_000);
    }

    [Fact]
    public void Parse_StripsFootnoteMarkers()
    {
        using var wb = Workbook(ws => Row(ws, 9, "Air carrier, domestic, all services¹", 3, 3, 3, 3, 3));
        // Two rows now clean to the same label; both are summed.
        var air = Assert.Single(Parse(wb), r => r.Mode == "aviation" && r.Year == 2019);
        Assert.Equal(4_000_000, air.Value);
        using var wb2 = Workbook(ws => Row(ws, 9, "Light duty vehicle, long wheel base,a", 1, 1, 1, 1, 1));
        Assert.Equal(17_000_000, Assert.Single(Parse(wb2), r => r.VehicleClass == "light_duty" && r.Year == 2020).Value);
    }

    [Fact]
    public void Parse_PartialSum_EmitsNoRowForThatYear()
    {
        // long wheel base has no value for 2021: light_duty 2021 would be understated, so it is dropped.
        using var wb = Workbook(ws => ws.Cell(7, 4).Value = "U");
        var (rows, partials) = BtsNtsTableAdapter.Aggregate(BtsNtsTableAdapter.ParseCells(wb, "1-40"), Map, "passenger_miles", 1_000_000);
        Assert.DoesNotContain(rows, r => r.VehicleClass == "light_duty" && r.Year == 2021);
        Assert.Contains(rows, r => r.VehicleClass == "light_duty" && r.Year == 2022);
        Assert.Equal([("highway", "light_duty", 2021)], partials);
    }

    [Fact]
    public async Task Ingest_PartialSum_IsCountedUnmapped()
    {
        Drop("t.xlsx", Workbook(ws => ws.Cell(7, 4).Value = "U"));
        var run = await RunAsync(Adapter());
        var u = (await QueryAsync($"select unmapped::text, status from ingest_run where ingest_run_id = '{run}'"))[0];
        Assert.Equal("succeeded", u[1]);
        Assert.Contains("\"partial_sum:highway/light_duty/2021\": 1", (string)u[0]);
        Assert.Equal(0L, (await QueryAsync($"select count(*) from exposure where source_id = '{SourceId}' and vehicle_class = 'light_duty' and year = 2021"))[0][0]);
    }

    [Fact]
    public void Parse_NoYearHeader_Throws()
    {
        using var wb = new XLWorkbook();
        wb.AddWorksheet("x").Cell(1, 1).Value = "nothing here";
        Assert.Throws<InvalidDataException>(() => Parse(wb));
    }

    // ---- drop folder + database ----

    private BtsNtsTableAdapter Adapter()
    {
        var mappings = Directory.CreateDirectory(Path.Combine(_tmp, "data", "mappings"));
        File.WriteAllText(Path.Combine(mappings.FullName, "bts-test-pm.csv"),
            "row_label,mode,vehicle_class,regulation_class,provenance\n" +
            "\"Air carrier, domestic, all services\",aviation,,part_121,measured\n" +
            "\"Light duty vehicle, short wheel base\",highway,light_duty,,survey_estimate\n" +
            "\"Light duty vehicle, long wheel base\",highway,light_duty,,survey_estimate\n" +
            "Motorcycle,highway,motorcycle,,survey_estimate\n");
        var o = BtsTableOptions.PassengerMiles();
        o.SourceId = SourceId;
        o.MappingFile = "bts-test-pm.csv";
        o.InboxDir = Path.Combine(_tmp, "inbox");
        return new BtsNtsTableAdapter(o, Path.Combine(_tmp, "data"));
    }

    private void Drop(string name, XLWorkbook wb, DateTime? writeTime = null)
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "inbox"));
        var path = Path.Combine(_tmp, "inbox", name);
        wb.SaveAs(path);
        if (writeTime is { } t) File.SetLastWriteTimeUtc(path, t);
    }

    // A fresh raw-store root per run: RawStore never overwrites, and two runs can land in the same second.
    private async Task<Guid> RunAsync(BtsNtsTableAdapter a) =>
        await new IngestRunner(db.ConnectionString, new RawStore(Path.Combine(_tmp, "raw-" + Guid.NewGuid().ToString("N"))))
            .RunAsync(a, new IngestParameters(null), CancellationToken.None);

    private async Task<List<object[]>> QueryAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var r = await new NpgsqlCommand(sql, conn).ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await r.ReadAsync())
        {
            var row = new object[r.FieldCount];
            r.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    [Fact]
    public async Task Ingest_UpsertsExposureIdempotently()
    {
        var adapter = Adapter();
        Drop("t.xlsx", Workbook());
        var first = await RunAsync(adapter);
        var before = await QueryAsync($"select year, vehicle_class, value from exposure where source_id = '{SourceId}' order by mode, year, vehicle_class");
        var second = await RunAsync(adapter);
        var after = await QueryAsync($"select year, vehicle_class, value from exposure where source_id = '{SourceId}' order by mode, year, vehicle_class");

        var status = await QueryAsync($"select status, error from ingest_run where ingest_run_id in ('{first}', '{second}')");
        Assert.All(status, s => Assert.True((string)s[0] == "succeeded", s[1]?.ToString()));
        Assert.Equal(14, before.Count); // aviation 2019, 2020, 2022, 2023 + light_duty and motorcycle 2019..2023
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Select(r => (double)r[2]), after.Select(r => (double)r[2]));
        Assert.Equal(0L, (await QueryAsync($"select count(*) from exposure where source_id = '{SourceId}' and ingest_run_id <> '{second}'"))[0][0]);
        Assert.Equal(16_000_000.0, (double)(await QueryAsync(
            $"select value from exposure where source_id = '{SourceId}' and vehicle_class = 'light_duty' and year = 2020"))[0][0]);
        // staging is replaced, not appended
        Assert.Equal(24L, (await QueryAsync("select count(*) from stg_bts.bts_test_pm"))[0][0]);
    }

    [Fact]
    public async Task Fetch_NoFile_NamesTableAndFolder()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Adapter().FetchAsync(new RawStore(Path.Combine(_tmp, "raw")), new IngestParameters(null), CancellationToken.None));
        Assert.Contains("NTS Table 1-40 'U.S. Passenger-Miles'", ex.Message);
        Assert.Contains(Path.Combine(_tmp, "inbox"), ex.Message);
    }

    [Fact]
    public async Task Fetch_PicksNewestXlsx()
    {
        var adapter = Adapter();
        Drop("old.xlsx", Workbook(), DateTime.UtcNow.AddDays(-5));
        Drop("new.xlsx", Workbook(), DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(Path.Combine(_tmp, "inbox", "newer-but-not-xlsx.csv"), "a,b");
        var files = await adapter.FetchAsync(new RawStore(Path.Combine(_tmp, "raw")), new IngestParameters(null), CancellationToken.None);
        Assert.Equal("new.xlsx", Assert.Single(files).Name);
    }

    [Fact]
    public async Task Host_RegistersBothBtsAdapters()
    {
        await using var factory = new ApiFactory(db);
        var adapters = factory.Services.GetServices<ISourceAdapter>().ToList();
        foreach (var (id, kind) in new[] { ("bts-passenger-miles", "multi"), ("bts-vehicle-miles", "multi") })
        {
            var a = Assert.Single(adapters, x => x.Descriptor.SourceId == id);
            Assert.IsType<BtsNtsTableAdapter>(a);
            Assert.Equal(kind, a.Descriptor.Mode);
            Assert.Equal("exposure", a.Descriptor.Kind);
        }
        Assert.Equal("https://www.bts.gov/content/us-vehicle-miles",
            adapters.Single(x => x.Descriptor.SourceId == "bts-vehicle-miles").Descriptor.Url);
    }

    private sealed class StubAdapter(string id) : ISourceAdapter
    {
        public SourceDescriptor Descriptor { get; } = new(id, "Stub", "batch", "events", "", "", "");
        public Task<IReadOnlyList<RawFile>> FetchAsync(IRawStore raw, IngestParameters p, CancellationToken ct) => throw new NotImplementedException();
        public Task StageAsync(IReadOnlyList<RawFile> files, NpgsqlConnection conn, IngestParameters p, CancellationToken ct) => throw new NotImplementedException();
        public Task<NormalizeResult> NormalizeAsync(NpgsqlConnection conn, Guid runId, IngestParameters p, CancellationToken ct) => throw new NotImplementedException();
    }

    [Fact]
    public async Task ApiFactory_AdapterReplacesRegisteredOneWithSameSourceId()
    {
        var stub = new StubAdapter("bts-vehicle-miles");
        await using var factory = new ApiFactory(db, null, stub);
        var adapters = factory.Services.GetServices<ISourceAdapter>().ToList();
        Assert.Same(stub, Assert.Single(adapters, a => a.Descriptor.SourceId == "bts-vehicle-miles"));
        Assert.Contains(adapters, a => a.Descriptor.SourceId == "bts-passenger-miles");
        Assert.Contains(adapters, a => a.Descriptor.SourceId == "fars");
    }
}
