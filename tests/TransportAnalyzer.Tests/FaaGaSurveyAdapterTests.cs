// FAA GA Survey adapter tests over in-memory ClosedXML workbooks that mirror the real Chapter 1 layout
// (header in row 2, newest year first, alternating "% Std. Error" rows, repeated labels, footnote digits).
using System.Net;
using ClosedXML.Excel;
using Microsoft.Extensions.Options;
using Npgsql;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;
using TransportAnalyzer.Ingestion;
using TransportAnalyzer.Ingestion.Faa;

namespace TransportAnalyzer.Tests;

[Collection("db")]
public class FaaGaSurveyAdapterTests(PostgresFixture db) : IDisposable
{
    private const string SourceId = "faa-ga-survey";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ta-faa-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Sheets = ["1.3", "1.4"];

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
    }

    private static readonly string RealMapPath = Path.Combine(FindRepoRoot(), "data", "mappings", "faa-ga-survey.csv");

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "data", "mappings", "faa-ga-survey.csv"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static IReadOnlyList<FaaMap> RealMap() => FaaGaSurveyAdapter.LoadMap(RealMapPath);

    // value rows: label -> (2024, 2023, 2022) hours in thousands, with % std error
    private static void Pair(IXLWorksheet ws, int r, string label, double[] v, double[] pct)
    {
        ws.Cell(r, 1).Value = label;
        ws.Cell(r + 1, 1).Value = "% Std. Error";
        for (var i = 0; i < v.Length; i++) { ws.Cell(r, i + 2).Value = v[i]; ws.Cell(r + 1, i + 2).Value = pct[i]; }
    }

    private static void Plain(IXLWorksheet ws, int r, string label, params double[] v)
    {
        ws.Cell(r, 1).Value = label;
        for (var i = 0; i < v.Length; i++) ws.Cell(r, i + 2).Value = v[i];
    }

    private static XLWorkbook Workbook(Action<IXLWorksheet, IXLWorksheet>? tweak = null)
    {
        var wb = new XLWorkbook();
        var decoy = wb.AddWorksheet("Chart 1");
        decoy.Cell(1, 1).Value = "Chart";
        var t13 = wb.AddWorksheet("1.3");
        t13.Cell(1, 1).Value = "Table 1.3 - HOURS IN THOUSANDS";
        t13.Cell(2, 1).Value = "AIRCRAFT TYPE";
        for (var i = 0; i < 3; i++) t13.Cell(2, i + 2).Value = 2024 - i;
        for (var i = 3; i < 6; i++) t13.Cell(2, i + 2).Value = 2024 - i; // 2021..2019 so the header has >= 5 years
        Pair(t13, 3, "Fixed Wing: Total", [100, 90, 80, 70, 60, 50], [1, 1, 1, 1, 1, 1]);
        Pair(t13, 5, "Piston: Total", [10, 20, 30, 40, 50, 60], [2, 2, 2, 2, 2, 2]);
        Pair(t13, 7, "Turboprop: Total", [5, 5, 5, 5, 5, 5], [1, 1, 1, 1, 1, 1]);
        Pair(t13, 9, "Turbojet: Total", [6, 6, 6, 6, 6, 6], [1, 1, 1, 1, 1, 1]);
        Pair(t13, 11, "Rotorcraft: Total", [7, 7, 7, 7, 7, 7], [1, 1, 1, 1, 1, 1]);
        Pair(t13, 13, "Piston: Total", [999, 999, 999, 999, 999, 999], [9, 9, 9, 9, 9, 9]); // rotorcraft piston, occurrence 2
        Pair(t13, 15, "Gliders", [30, 30, 30, 30, 30, 30], [10, 10, 10, 10, 10, 10]);
        Pair(t13, 17, "Lighter-than-air", [40, 40, 40, 40, 40, 40], [5, 5, 5, 5, 5, 5]);
        Pair(t13, 19, "Experimental: Total1", [8, 8, 8, 8, 8, 8], [4, 4, 4, 4, 4, 4]);
        Pair(t13, 21, "Special Light-sport", [3, 3, 3, 3, 3, 3], [4, 4, 4, 4, 4, 4]);
        Pair(t13, 23, "All Aircraft", [200, 200, 200, 200, 200, 200], [1, 1, 1, 1, 1, 1]);
        t13.Cell(25, 1).Value = "Table Notes:";
        var t14 = wb.AddWorksheet("1.4");
        t14.Cell(1, 1).Value = "Table 1.4";
        t14.Cell(2, 1).Value = "General Aviation Use";
        for (var i = 0; i < 6; i++) t14.Cell(2, i + 2).Value = 2024 - i;
        Plain(t14, 3, "Personal", 1, 1, 1, 1, 1, 1);
        Plain(t14, 4, "Aerial Application Agriculture1", 20, 21, 22, 23, 24, 25);
        Plain(t14, 5, "Aerial Application Other", 5, 5, 5, 5, 5, 5);
        Plain(t14, 6, "External Load", 2, 2, 2, 2, 2, 2);
        Plain(t14, 7, "General Aviation Use Total", 1000, 1000, 1000, 1000, 1000, 1000);
        t14.Cell(8, 1).Value = "On-Demand FAR Part 135 Use";
        Plain(t14, 9, "Air Taxi", 1, 1, 1, 1, 1, 1);
        Plain(t14, 10, "On-Demand FAR Part 135 Use Total", 400, 410, 420, 430, 440, 450);
        tweak?.Invoke(t13, t14);
        return wb;
    }

    private static List<ExposureRow> ParseRows(XLWorkbook wb) =>
        FaaGaSurveyAdapter.Parse(wb, RealMap(), Sheets, 1000).Rows;

    private static ExposureRow One(IEnumerable<ExposureRow> rows, int year, string? vc, string? rc) =>
        Assert.Single(rows, r => r.Year == year && r.VehicleClass == vc && r.RegulationClass == rc);

    [Fact]
    public void Parse13_UsesFirstOccurrenceAndScalesUnits()
    {
        var piston = One(ParseRows(Workbook()), 2024, "light_aircraft", "general_aviation");
        Assert.Equal(10_000, piston.Value);
        Assert.Equal("aviation", piston.Mode);
        Assert.Equal("flight_hours", piston.Metric);
        Assert.Equal("survey_estimate", piston.Provenance);
        Assert.Equal(60_000, One(ParseRows(Workbook()), 2019, "light_aircraft", "general_aviation").Value);
        Assert.Equal(8_000, One(ParseRows(Workbook()), 2024, "experimental", "general_aviation").Value); // footnote digit stripped
        Assert.Equal(200_000, One(ParseRows(Workbook()), 2024, null, "general_aviation").Value);
    }

    [Fact]
    public void Parse13_StandardErrorIsAbsolute()
    {
        var piston = One(ParseRows(Workbook()), 2024, "light_aircraft", "general_aviation");
        Assert.Equal(10_000 * 2 / 100.0, piston.StandardError!.Value, 6);
        Assert.Null(piston.Assumptions);
    }

    [Fact]
    public void Parse13_OtherSumsGlidersAndLta_SeInQuadrature()
    {
        var other = One(ParseRows(Workbook()), 2024, "other", "general_aviation");
        Assert.Equal(70_000, other.Value);
        var gl = 30_000 * 10 / 100.0; var lta = 40_000 * 5 / 100.0;
        Assert.Equal(Math.Sqrt(gl * gl + lta * lta), other.StandardError!.Value, 6);
        Assert.Contains("Gliders + Lighter-than-air", other.Assumptions);
    }

    [Fact]
    public void Parse13_MissingComponentSe_GivesNullSeButKeepsValue()
    {
        using var wb = Workbook((t13, _) => t13.Cell(16, 2).Clear());
        var other = One(ParseRows(wb), 2024, "other", "general_aviation");
        Assert.Equal(70_000, other.Value);
        Assert.Null(other.StandardError);
        Assert.NotNull(One(ParseRows(wb), 2023, "other", "general_aviation").StandardError);
    }

    [Fact]
    public void Parse14_DerivesPart91AndPart137()
    {
        var rows = ParseRows(Workbook());
        var p137 = One(rows, 2024, null, "part_137");
        Assert.Equal(25_000, p137.Value);
        Assert.Equal("derived", p137.Provenance);
        Assert.NotNull(p137.Assumptions);
        var p133 = One(rows, 2024, null, "part_133");
        Assert.Equal(2_000, p133.Value);
        Assert.Equal("derived", p133.Provenance);
        var p91 = One(rows, 2024, null, "part_91");
        Assert.Equal((1000 - 20 - 5 - 2) * 1000.0, p91.Value);
        Assert.Equal("derived", p91.Provenance);
        Assert.Null(p91.StandardError);
        Assert.Equal("{\"rule\":\"General Aviation Use Total − Aerial Application Agriculture − Aerial Application Other − External Load\"}",
            p91.Assumptions);
        var p135 = One(rows, 2024, null, "part_135");
        Assert.Equal(400_000, p135.Value);
        Assert.Equal("survey_estimate", p135.Provenance);
    }

    [Fact]
    public void Parse_SpecialLightSport_IsReportedUnmapped()
    {
        var result = FaaGaSurveyAdapter.Parse(Workbook(), RealMap(), Sheets, 1000);
        Assert.Equal(1L, result.Unmapped["exposure:row_label=Special Light-sport"]);
        Assert.DoesNotContain(result.Rows, r => r.Value == 3_000);
    }

    [Fact]
    public void Parse_MappedLabelAbsentFromSheet_IsReported()
    {
        using var wb = Workbook((_, t14) => t14.Cell(6, 1).Value = "Renamed Load");
        var result = FaaGaSurveyAdapter.Parse(wb, RealMap(), Sheets, 1000);
        Assert.Equal(1L, result.Unmapped["exposure:row_label=External Load"]);
    }

    [Fact]
    public void Parse_PartialSum_EmitsNoRowAndCountsWithRegulationClass()
    {
        // External Load has no value for 2023: part_91 and part_133 for 2023 would be wrong, so none is written.
        using var wb = Workbook((_, t14) => t14.Cell(6, 3).Value = "U");
        var result = FaaGaSurveyAdapter.Parse(wb, RealMap(), Sheets, 1000);
        Assert.DoesNotContain(result.Rows, r => r.Year == 2023 && r.RegulationClass == "part_91");
        Assert.Contains(result.Rows, r => r.Year == 2024 && r.RegulationClass == "part_91");
        Assert.Equal(1L, result.Unmapped["partial_sum:aviation//part_91/2023"]);
    }

    [Fact]
    public void Parse_TwoMappingGroupsWithSameExposureKey_ThrowsNamingKey()
    {
        // Different tables, same (mode, vehicle_class, regulation_class): both would write the same exposure key.
        IReadOnlyList<FaaMap> map =
        [
            new("1.3", "Gliders", 1, "value", "aviation", "glider", null, "reported"),
            new("1.4", "Personal", 1, "value", "aviation", "glider", null, "reported"),
        ];
        using var wb = Workbook();
        var ex = Assert.Throws<InvalidDataException>(() => FaaGaSurveyAdapter.Parse(wb, map, Sheets, 1000));
        Assert.Contains("aviation", ex.Message);
        Assert.Contains("glider", ex.Message);
        Assert.Contains("year=2019", ex.Message); // first (lowest) duplicated year
        Assert.Contains("flight_hours", ex.Message);
    }

    [Fact]
    public void Parse_NoSheet_Throws()
    {
        using var wb = new XLWorkbook();
        wb.AddWorksheet("x");
        Assert.Throws<InvalidDataException>(() => ParseRows(wb));
    }

    // ---- fetch + database ----

    private sealed class Handler(byte[] body) : HttpMessageHandler
    {
        public string? LastUrl;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private static byte[] Bytes(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private FaaGaSurveyAdapter Adapter(XLWorkbook wb, string? mappingCsv = null, Handler? handler = null)
    {
        var mappings = Directory.CreateDirectory(Path.Combine(_tmp, "data", "mappings"));
        File.WriteAllText(Path.Combine(mappings.FullName, "faa-ga-survey.csv"), mappingCsv ?? File.ReadAllText(RealMapPath));
        return new FaaGaSurveyAdapter(new HttpClient(handler ?? new Handler(Bytes(wb))),
            Options.Create(new FaaGaSurveyOptions()), Path.Combine(_tmp, "data"));
    }

    private async Task<Guid> RunAsync(FaaGaSurveyAdapter a, IReadOnlyList<int>? years = null) =>
        await new IngestRunner(db.ConnectionString, new RawStore(Path.Combine(_tmp, "raw-" + Guid.NewGuid().ToString("N"))))
            .RunAsync(a, new IngestParameters(years), CancellationToken.None);

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
    public async Task Ingest_WritesRowsWithSeAndAssumptions()
    {
        await QueryAsync($"delete from exposure where source_id = '{SourceId}'");
        var handler = new Handler(Bytes(Workbook()));
        var run = await RunAsync(Adapter(null!, handler: handler));
        Assert.Contains("2024GASurveyCh1.xlsx", handler.LastUrl);
        var status = (await QueryAsync($"select status, error, unmapped::text from ingest_run where ingest_run_id = '{run}'"))[0];
        Assert.True((string)status[0] == "succeeded", status[1]?.ToString());
        Assert.Contains("exposure:row_label=Special Light-sport", (string)status[2]);

        var p91 = (await QueryAsync($"select value, standard_error, assumptions::text, provenance from exposure where source_id = '{SourceId}' and year = 2024 and regulation_class = 'part_91'"))[0];
        Assert.Equal(973_000.0, (double)p91[0]);
        Assert.IsType<DBNull>(p91[1]);
        Assert.Contains("External Load", (string)p91[2]);
        Assert.Equal("derived", p91[3]);
        var piston = (await QueryAsync($"select standard_error, assumptions from exposure where source_id = '{SourceId}' and year = 2024 and vehicle_class = 'light_aircraft'"))[0];
        Assert.Equal(200.0, (double)piston[0], 6);
        Assert.IsType<DBNull>(piston[1]);
        Assert.Equal(66L, (await QueryAsync($"select count(*) from exposure where source_id = '{SourceId}'"))[0][0]);
    }

    [Fact]
    public async Task Ingest_ReplacesSourceRowsForYears()
    {
        await QueryAsync($"delete from exposure where source_id = '{SourceId}'");
        await RunAsync(Adapter(null!, handler: new Handler(Bytes(Workbook()))));
        Assert.Equal(6L, (await QueryAsync($"select count(*) from exposure where source_id = '{SourceId}' and vehicle_class = 'turboprop'"))[0][0]);

        // Mapping correction: Turboprop now maps to another regulation class; the old key goes away for 2022 only.
        var corrected = File.ReadAllText(RealMapPath).Replace("aviation,turboprop,general_aviation", "aviation,turboprop,part_91");
        await RunAsync(Adapter(null!, corrected, new Handler(Bytes(Workbook()))), [2022]);
        var years = await QueryAsync($"select year from exposure where source_id = '{SourceId}' and vehicle_class = 'turboprop' and regulation_class = 'general_aviation' order by year");
        Assert.Equal([2019, 2020, 2021, 2023, 2024], years.Select(r => (int)r[0]));
        var moved = await QueryAsync($"select year from exposure where source_id = '{SourceId}' and vehicle_class = 'turboprop' and regulation_class = 'part_91'");
        Assert.Equal([2022], moved.Select(r => (int)r[0]));
    }
}
