using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Ingestion.Bts;

namespace TransportAnalyzer.Ingestion.Faa;

/// <summary>One line of <c>faa-ga-survey.csv</c>. <c>Operation</c> is <c>value</c> or <c>add</c> (added into the key),
/// <c>subtract</c>, or <c>unmapped</c> (a sheet row deliberately not mapped, reported when present).</summary>
internal sealed record FaaMap(string Table, string RowLabel, int Occurrence, string Operation, string? Mode,
    string? VehicleClass, string? RegulationClass, string? Provenance);

/// <summary>A numeric cell of a value row, with the row's % standard error for the same year when present.</summary>
internal sealed record FaaCell(string Table, string Label, int Occurrence, int Year, double Value, double? SePct);

internal sealed record FaaAggregate(List<ExposureRow> Rows, Dictionary<string, long> Unmapped);

/// <summary>FAA General Aviation and Part 135 Activity Survey, chapter 1 (flight hours by aircraft type, table 1.3, and
/// by use, table 1.4). The xlsx is downloaded into the raw store, every numeric cell is staged to
/// <c>stg_faa.faa_ga_survey</c>, and normalize maps (table, row label, occurrence) to exposure keys with the mapping file.</summary>
public sealed partial class FaaGaSurveyAdapter(HttpClient http, IOptions<FaaGaSurveyOptions> o, string dataRoot) : ISourceAdapter
{
    private const string StagingTable = "stg_faa.faa_ga_survey";
    private const string Metric = "flight_hours";
    private const string SeLabel = "% Std. Error";
    private readonly FaaGaSurveyOptions _options = o.Value;

    public SourceDescriptor Descriptor { get; } = new("faa-ga-survey", "FAA General Aviation and Part 135 Activity Survey",
        "aviation", "exposure", o.Value.Url,
        "Flight hours: hours flown by general aviation and on-demand Part 135 aircraft, by aircraft type and by use. The survey tables are in thousands of hours; stored as hours.",
        "Survey estimates. Covers Part 91, on-demand Part 135, 137 and 133 flying only — not airlines (Part 121) and not scheduled Part 135. " +
        "Part 91 / 137 hours are derived by subtracting use categories (see assumptions).");

    public async Task<IReadOnlyList<RawFile>> FetchAsync(IRawStore raw, IngestParameters p, CancellationToken ct)
    {
        var uri = new Uri(_options.Url);
        var name = Path.GetFileName(uri.AbsolutePath);
        using var res = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using var body = await res.Content.ReadAsStreamAsync(ct);
        return [await raw.SaveAsync(Descriptor.SourceId, name.Length > 0 ? name : "faa-ga-survey.xlsx", body, ct)];
    }

    public async Task StageAsync(IReadOnlyList<RawFile> files, NpgsqlConnection conn, IngestParameters p, CancellationToken ct)
    {
        List<FaaCell> cells;
        using (var wb = new XLWorkbook(files.Single().Path))
            cells = ParseCells(wb, _options.Sheets).ToList();

        await using (var ddl = new NpgsqlCommand(
            "create schema if not exists stg_faa; " +
            $"create table if not exists {StagingTable} (sheet text, row_label text, occurrence text, year text, value text, se_pct text); " +
            $"delete from {StagingTable}", conn))
            await ddl.ExecuteNonQueryAsync(ct);

        static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        await using var ins = new NpgsqlCommand(
            $"insert into {StagingTable} (sheet, row_label, occurrence, year, value, se_pct) select * from unnest(@s, @l, @o, @y, @v, @e)", conn);
        ins.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cells.Select(c => c.Table).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cells.Select(c => c.Label).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cells.Select(c => c.Occurrence.ToString(CultureInfo.InvariantCulture)).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("y", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cells.Select(c => c.Year.ToString(CultureInfo.InvariantCulture)).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cells.Select(c => Num(c.Value)).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cells.Select(c => c.SePct is { } e ? Num(e) : null).ToArray() });
        await ins.ExecuteNonQueryAsync(ct);
    }

    public async Task<NormalizeResult> NormalizeAsync(NpgsqlConnection conn, Guid runId, IngestParameters p, CancellationToken ct)
    {
        var map = LoadMap(Path.Combine(dataRoot, "mappings", _options.MappingFile));

        var cells = new List<FaaCell>();
        await using (var q = new NpgsqlCommand($"select sheet, row_label, occurrence, year, value, se_pct from {StagingTable}", conn))
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                cells.Add(new FaaCell(r.GetString(0), r.GetString(1), int.Parse(r.GetString(2), CultureInfo.InvariantCulture),
                    int.Parse(r.GetString(3), CultureInfo.InvariantCulture), double.Parse(r.GetString(4), CultureInfo.InvariantCulture),
                    r.IsDBNull(5) ? null : double.Parse(r.GetString(5), CultureInfo.InvariantCulture)));

        var years = p.Years is { Count: > 0 } ? p.Years.ToHashSet() : null;
        var result = Aggregate(cells, map, Metric, _options.UnitMultiplier, years);

        // The run's rows replace the source's rows for the years it covers (inside the runner's transaction).
        await using (var del = new NpgsqlCommand(
            "delete from exposure where source_id = @src and (@years::int[] is null or year = any(@years))", conn))
        {
            del.Parameters.AddWithValue("src", Descriptor.SourceId);
            del.Parameters.Add(new NpgsqlParameter("years", NpgsqlDbType.Array | NpgsqlDbType.Integer)
                { Value = (object?)years?.ToArray() ?? DBNull.Value });
            await del.ExecuteNonQueryAsync(ct);
        }

        var written = 0L;
        foreach (var row in result.Rows)
        {
            await using var cmd = new NpgsqlCommand("""
                insert into exposure (exposure_id, source_id, mode, year, vehicle_class, regulation_class, metric, value, provenance,
                                      standard_error, assumptions, ingest_run_id)
                values (gen_random_uuid(), @src, @mode, @year, @vc, @rc, @metric, @value, @prov, @se, @assump::jsonb, @run)
                on conflict (source_id, mode, year, vehicle_class, regulation_class, metric)
                do update set value = excluded.value, provenance = excluded.provenance, standard_error = excluded.standard_error,
                              assumptions = excluded.assumptions, ingest_run_id = excluded.ingest_run_id
                """, conn);
            cmd.Parameters.AddWithValue("src", Descriptor.SourceId);
            cmd.Parameters.AddWithValue("mode", row.Mode);
            cmd.Parameters.AddWithValue("year", row.Year);
            cmd.Parameters.Add(new NpgsqlParameter("vc", NpgsqlDbType.Text) { Value = (object?)row.VehicleClass ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Text) { Value = (object?)row.RegulationClass ?? DBNull.Value });
            cmd.Parameters.AddWithValue("metric", row.Metric);
            cmd.Parameters.AddWithValue("value", row.Value);
            cmd.Parameters.AddWithValue("prov", row.Provenance);
            cmd.Parameters.Add(new NpgsqlParameter("se", NpgsqlDbType.Double) { Value = (object?)row.StandardError ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("assump", NpgsqlDbType.Text) { Value = (object?)row.Assumptions ?? DBNull.Value });
            cmd.Parameters.AddWithValue("run", runId);
            written += await cmd.ExecuteNonQueryAsync(ct);
        }
        return new NormalizeResult(new Dictionary<string, long> { ["exposure"] = written }, result.Unmapped);
    }

    /// <summary>Test entry point: parse a workbook straight to exposure rows and unmapped counts.</summary>
    internal static FaaAggregate Parse(XLWorkbook wb, IReadOnlyList<FaaMap> map, IEnumerable<string> sheets, double unitMultiplier) =>
        Aggregate(ParseCells(wb, sheets).ToList(), map, Metric, unitMultiplier);

    /// <summary>Every numeric cell under a year header of the named worksheets, keyed by (sheet, cleaned label, occurrence).
    /// Occurrence counts identical cleaned labels per sheet, top to bottom from 1; a "% Std. Error" row attaches to the value
    /// row directly above it and is not itself counted.</summary>
    internal static IEnumerable<FaaCell> ParseCells(XLWorkbook wb, IEnumerable<string> sheets)
    {
        foreach (var sheet in sheets)
        {
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name == sheet)
                ?? throw new InvalidDataException($"Worksheet '{sheet}' not found.");
            var used = ws.RangeUsed() ?? throw new InvalidDataException($"Worksheet '{sheet}' is empty.");
            var lastRow = used.LastRow().RowNumber();
            var lastCol = used.LastColumn().ColumnNumber();

            var headerRow = 0;
            var yearCols = new Dictionary<int, int>();
            for (var r = used.FirstRow().RowNumber(); r <= lastRow && headerRow == 0; r++)
            {
                var cols = new Dictionary<int, int>();
                for (var c = 2; c <= lastCol; c++)
                    if (BtsNtsTableAdapter.TryYear(ws.Cell(r, c), out var y)) cols[c] = y;
                if (cols.Count >= 5) { headerRow = r; yearCols = cols; }
            }
            if (headerRow == 0) throw new InvalidDataException($"No header row with at least five year columns found on sheet '{sheet}'.");

            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            (string Label, int Occ, int Row, Dictionary<int, double> Values, Dictionary<int, double> Se)? pending = null;

            IEnumerable<FaaCell> Flush()
            {
                if (pending is not { } v) yield break;
                foreach (var (year, value) in v.Values)
                    yield return new FaaCell(sheet, v.Label, v.Occ, year, value, v.Se.TryGetValue(year, out var s) ? s : null);
            }

            for (var r = headerRow + 1; r <= lastRow; r++)
            {
                var label = CleanLabel(ws.Cell(r, 1).GetFormattedString());
                if (label.Length == 0) continue;
                if (label.Equals(SeLabel, StringComparison.OrdinalIgnoreCase))
                {
                    if (pending is { } p && p.Row == r - 1)
                        foreach (var (col, year) in yearCols)
                            if (BtsNtsTableAdapter.TryNumber(ws.Cell(r, col), out var pct)) p.Se[year] = pct;
                    continue;
                }
                foreach (var cell in Flush()) yield return cell;
                seen[label] = seen.GetValueOrDefault(label) + 1;
                var values = new Dictionary<int, double>();
                foreach (var (col, year) in yearCols)
                    if (BtsNtsTableAdapter.TryNumber(ws.Cell(r, col), out var v)) values[year] = v;
                pending = (label, seen[label], r, values, new Dictionary<int, double>());
            }
            foreach (var cell in Flush()) yield return cell;
        }
    }

    /// <summary>Maps cells to exposure keys. Mapping rows sharing (table, mode, vehicle_class, regulation_class) are combined in
    /// file order (<c>value</c>/<c>add</c> add, <c>subtract</c> subtracts). If a component has no value for a year the key/year
    /// is dropped and counted as <c>partial_sum:{mode}/{vehicle_class}/{regulation_class}/{year}</c>.</summary>
    internal static FaaAggregate Aggregate(IEnumerable<FaaCell> cells, IReadOnlyList<FaaMap> map, string metric,
        double unitMultiplier, ISet<int>? years = null)
    {
        var cellList = cells.ToList();
        var byRow = cellList.GroupBy(c => (c.Table, Label: c.Label.ToUpperInvariant(), c.Occurrence))
            .ToDictionary(g => g.Key, g => g.ToDictionary(c => c.Year));
        IReadOnlyDictionary<int, FaaCell> Find(FaaMap m) =>
            byRow.TryGetValue((m.Table, CleanLabel(m.RowLabel).ToUpperInvariant(), m.Occurrence), out var d)
                ? d : new Dictionary<int, FaaCell>();

        var rows = new List<ExposureRow>();
        var unmapped = new Dictionary<string, long>();

        // Mapped rows absent from the sheet (layout drift), and deliberately unmapped rows that are present.
        foreach (var m in map)
        {
            var present = Find(m).Count > 0;
            var name = m.Occurrence > 1 ? $"{m.RowLabel}#{m.Occurrence}" : m.RowLabel;
            if (m.Operation == "unmapped" ? present : !present) unmapped[$"exposure:row_label={name}"] = 1;
        }

        foreach (var key in map.Where(m => m.Operation != "unmapped").GroupBy(m => (m.Table, m.Mode, m.VehicleClass, m.RegulationClass)))
        {
            var parts = key.Select(m => (Map: m, Sign: m.Operation == "subtract" ? -1.0 : 1.0, Cells: Find(m))).ToList();
            var provenance = parts[0].Map.Provenance ?? "";
            var mode = key.Key.Mode ?? throw new InvalidDataException($"Mapping row '{parts[0].Map.RowLabel}' has no mode.");
            string? assumptions = null;
            if (parts.Count > 1 || provenance == "derived")
            {
                var rule = parts[0].Map.RowLabel + string.Concat(parts.Skip(1).Select(x => (x.Sign < 0 ? " − " : " + ") + x.Map.RowLabel));
                assumptions = JsonSerializer.Serialize(new Dictionary<string, string> { ["rule"] = rule }, RuleJson);
            }

            foreach (var year in parts.SelectMany(x => x.Cells.Keys).Distinct().Order())
            {
                if (years is not null && !years.Contains(year)) continue;
                if (parts.Any(x => !x.Cells.ContainsKey(year)))
                {
                    unmapped[$"partial_sum:{mode}/{key.Key.VehicleClass}/{key.Key.RegulationClass}/{year}"] = 1;
                    continue;
                }
                var value = parts.Sum(x => x.Sign * x.Cells[year].Value) * unitMultiplier;
                double? se = null;
                if (parts.All(x => x.Cells[year].SePct is not null))
                    se = Math.Sqrt(parts.Sum(x => Math.Pow(x.Cells[year].Value * unitMultiplier * x.Cells[year].SePct!.Value / 100.0, 2)));
                rows.Add(new ExposureRow(mode, year, key.Key.VehicleClass, key.Key.RegulationClass, metric, value, provenance, se, assumptions));
            }
        }
        return new FaaAggregate(rows, unmapped);
    }

    private static readonly JsonSerializerOptions RuleJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly HashSet<string> Operations = ["value", "add", "subtract", "unmapped"];

    internal static IReadOnlyList<FaaMap> LoadMap(string path)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        csv.Read();
        csv.ReadHeader();
        var rows = new List<FaaMap>();
        while (csv.Read())
        {
            var op = csv.GetField("operation")!.Trim().ToLowerInvariant();
            if (!Operations.Contains(op)) throw new InvalidDataException($"{path}: unknown operation '{op}'.");
            rows.Add(new FaaMap(csv.GetField("table")!.Trim(), csv.GetField("row_label")!.Trim(),
                int.Parse(csv.GetField("occurrence")!.Trim(), CultureInfo.InvariantCulture), op,
                Blank(csv.GetField("mode")), Blank(csv.GetField("vehicle_class")), Blank(csv.GetField("regulation_class")),
                Blank(csv.GetField("provenance"))));
        }
        return rows;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Trims, collapses whitespace and strips glued-on footnote digits ("Experimental: Total1").
    /// Leading digits ("1 Engine: Total") are part of the label and stay.</summary>
    internal static string CleanLabel(string raw) => TrailingFootnoteDigits().Replace(BtsNtsTableAdapter.CleanLabel(raw), "").TrimEnd();

    [GeneratedRegex(@"(?<=[A-Za-z:.)])\d+$")] private static partial Regex TrailingFootnoteDigits();
}
