using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using Npgsql;
using NpgsqlTypes;
using TransportAnalyzer.Domain;

namespace TransportAnalyzer.Ingestion.Bts;

/// <summary>One BTS National Transportation Statistics xlsx table (years across, one row label per series),
/// downloaded by hand into the drop folder. Every numeric cell is staged to <c>stg_bts.{table}</c>; normalize
/// maps row labels to exposure keys with the mapping file, sums rows that share a key, scales units and replaces
/// the source's <c>exposure</c> rows for the run's years.</summary>
public sealed partial class BtsNtsTableAdapter(BtsTableOptions o, string dataRoot) : ISourceAdapter
{
    private const string StagingSchema = "stg_bts";

    public SourceDescriptor Descriptor { get; } = new(o.SourceId, o.Name, "multi", "exposure", o.Url,
        o.Metric == "passenger_miles"
            ? "Passenger-miles: the number of passengers times the miles they travel, by mode, in millions."
            : "Vehicle-miles: the miles travelled by vehicles, by mode and vehicle class, in millions.",
        "Highway passenger-miles are BTS estimates from vehicle-miles and average occupancy. " +
        "Light-duty vehicles are not split into cars and light trucks after 2006.");

    private string StagingTable => StagingSchema + "." + o.SourceId.Replace('-', '_');

    public Task<IReadOnlyList<RawFile>> FetchAsync(IRawStore raw, IngestParameters p, CancellationToken ct) =>
        FetchCoreAsync(raw, ct);

    private async Task<IReadOnlyList<RawFile>> FetchCoreAsync(IRawStore raw, CancellationToken ct)
    {
        var dir = Path.GetFullPath(o.InboxDir);
        var newest = Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles("*.xlsx").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (newest is null)
            throw new InvalidOperationException($"Download {o.TableLabel} as xlsx from bts.gov into {dir}");
        await using var content = newest.OpenRead();
        return [await raw.SaveAsync(o.SourceId, newest.Name, content, ct)];
    }

    public async Task StageAsync(IReadOnlyList<RawFile> files, NpgsqlConnection conn, IngestParameters p, CancellationToken ct)
    {
        var file = files.Single();
        List<(string Label, int Year, double Value)> cells;
        using (var wb = new XLWorkbook(file.Path))
            cells = ParseCells(wb, o.SheetName).ToList();

        await using (var ddl = new NpgsqlCommand(
            $"create schema if not exists {StagingSchema}; " +
            $"create table if not exists {StagingTable} (row_label text, year text, value text); " +
            $"delete from {StagingTable}", conn))
            await ddl.ExecuteNonQueryAsync(ct);

        await using var ins = new NpgsqlCommand(
            $"insert into {StagingTable} (row_label, year, value) select * from unnest(@l, @y, @v)", conn);
        ins.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cells.Select(c => c.Label).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("y", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cells.Select(c => c.Year.ToString(CultureInfo.InvariantCulture)).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = cells.Select(c => c.Value.ToString("R", CultureInfo.InvariantCulture)).ToArray() });
        await ins.ExecuteNonQueryAsync(ct);
    }

    public async Task<NormalizeResult> NormalizeAsync(NpgsqlConnection conn, Guid runId, IngestParameters p, CancellationToken ct)
    {
        var map = LoadMap(Path.Combine(dataRoot, "mappings", o.MappingFile));

        var cells = new List<(string, int, double)>();
        await using (var q = new NpgsqlCommand($"select row_label, year, value from {StagingTable}", conn))
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                cells.Add((r.GetString(0), int.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                    double.Parse(r.GetString(2), CultureInfo.InvariantCulture)));

        var years = p.Years is { Count: > 0 } ? p.Years.ToHashSet() : null;
        var (aggregated, partials) = Aggregate(cells, map, o.Metric, o.UnitMultiplier);
        var rows = aggregated.Where(r => years is null || years.Contains(r.Year)).ToList();

        // The run's rows replace the source's rows for the years it covers: a key dropped by a mapping correction,
        // the partial-sum guard or a new edition must not linger. Runs inside the runner's transaction.
        await using (var del = new NpgsqlCommand(
            "delete from exposure where source_id = @src and (@years::int[] is null or year = any(@years))", conn))
        {
            del.Parameters.AddWithValue("src", o.SourceId);
            del.Parameters.Add(new NpgsqlParameter("years", NpgsqlDbType.Array | NpgsqlDbType.Integer)
                { Value = (object?)years?.ToArray() ?? DBNull.Value });
            await del.ExecuteNonQueryAsync(ct);
        }

        var written = 0L;
        foreach (var row in rows)
        {
            await using var cmd = new NpgsqlCommand("""
                insert into exposure (exposure_id, source_id, mode, year, vehicle_class, regulation_class, metric, value, provenance, ingest_run_id)
                values (gen_random_uuid(), @src, @mode, @year, @vc, @rc, @metric, @value, @prov, @run)
                on conflict (source_id, mode, year, vehicle_class, regulation_class, metric)
                do update set value = excluded.value, provenance = excluded.provenance, ingest_run_id = excluded.ingest_run_id
                """, conn);
            cmd.Parameters.AddWithValue("src", o.SourceId);
            cmd.Parameters.AddWithValue("mode", row.Mode);
            cmd.Parameters.AddWithValue("year", row.Year);
            cmd.Parameters.Add(new NpgsqlParameter("vc", NpgsqlDbType.Text) { Value = (object?)row.VehicleClass ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Text) { Value = (object?)row.RegulationClass ?? DBNull.Value });
            cmd.Parameters.AddWithValue("metric", row.Metric);
            cmd.Parameters.AddWithValue("value", row.Value);
            cmd.Parameters.AddWithValue("prov", row.Provenance);
            cmd.Parameters.AddWithValue("run", runId);
            written += await cmd.ExecuteNonQueryAsync(ct);
        }

        // A mapped label that never appears in the sheet means the table layout drifted; surface it.
        var present = cells.Select(c => c.Item1).Distinct().ToList();
        var unmapped = map.Select(m => m.RowLabel).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(l => !present.Any(p => LabelMatches(p, l)))
            .ToDictionary(l => $"exposure:row_label={l}", _ => 1L);
        // R14: a key/year where only some of its summed rows have a value is dropped, never understated.
        foreach (var (mode, vehicleClass, year) in partials)
            if (years is null || years.Contains(year))
                unmapped[$"partial_sum:{mode}/{vehicleClass}/{year}"] = 1L;
        return new NormalizeResult(new Dictionary<string, long> { ["exposure"] = written }, unmapped);
    }

    /// <summary>Test entry point: parse a workbook straight to exposure rows.</summary>
    internal static IEnumerable<ExposureRow> Parse(XLWorkbook wb, IReadOnlyList<RowMap> map, string metric,
        double unitMultiplier, string? sheetName = null) =>
        Aggregate(ParseCells(wb, sheetName), map, metric, unitMultiplier).Rows;

    /// <summary>Every numeric cell under a year header on the named worksheet (first worksheet when null), keyed by
    /// row label. Cells such as U, N, R, empty or text are skipped.</summary>
    internal static IEnumerable<(string Label, int Year, double Value)> ParseCells(XLWorkbook wb, string? sheetName = null)
    {
        // The real downloads carry other sheets (a graph, an older edition with broken formulas); take the named one.
        var ws = sheetName is null
            ? wb.Worksheets.First()
            : wb.Worksheets.FirstOrDefault(w => w.Name == sheetName)
                ?? throw new InvalidDataException($"Worksheet '{sheetName}' not found.");
        var used = ws.RangeUsed();
        if (used is null) throw new InvalidDataException($"Worksheet '{ws.Name}' is empty.");

        var lastCol = used.LastColumn().ColumnNumber();
        var headerRow = 0;
        var yearCols = new Dictionary<int, int>();
        for (var r = used.FirstRow().RowNumber(); r <= used.LastRow().RowNumber() && headerRow == 0; r++)
        {
            var cols = new Dictionary<int, int>();
            for (var c = 1; c <= lastCol; c++)
                if (TryYear(ws.Cell(r, c), out var y)) cols[c] = y;
            if (cols.Count >= 5) { headerRow = r; yearCols = cols; }
        }
        if (headerRow == 0) throw new InvalidDataException("No header row with at least five year columns (1950-2100) found.");

        for (var r = headerRow + 1; r <= used.LastRow().RowNumber(); r++)
        {
            var label = CleanLabel(ws.Cell(r, 1).GetFormattedString());
            if (label.Length == 0) continue;
            foreach (var (col, year) in yearCols)
                if (TryNumber(ws.Cell(r, col), out var v)) yield return (label, year, v);
        }
    }

    /// <summary>Maps cells to exposure keys. A mapping row contributes once per year (its matching sheet rows are
    /// summed); mapping rows sharing a key are summed. When only some of a key's mapping rows have a value for a year,
    /// no row is produced and the key/year is returned in <c>Partials</c>.</summary>
    internal static (List<ExposureRow> Rows, List<(string Mode, string VehicleClass, int Year)> Partials) Aggregate(
        IEnumerable<(string Label, int Year, double Value)> cells, IReadOnlyList<RowMap> map, string metric, double unitMultiplier)
    {
        var cellList = cells.ToList();
        var labels = cellList.Select(c => c.Label).Distinct().ToList();
        // per distinct mapping label: the sheet labels it matches
        var matches = map.Select(m => m.RowLabel).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(l => l, l => labels.Where(x => LabelMatches(x, l)).ToHashSet(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<ExposureRow>();
        var partials = new List<(string, string, int)>();
        var keys = map.GroupBy(m => (m.Mode, m.VehicleClass, m.RegulationClass));
        foreach (var key in keys.OrderBy(k => k.Key.Mode).ThenBy(k => k.Key.VehicleClass).ThenBy(k => k.Key.RegulationClass))
        {
            var mapRows = key.GroupBy(m => m.RowLabel, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var years = cellList.Where(c => mapRows.Any(m => matches[m.RowLabel].Contains(c.Label)))
                .Select(c => c.Year).Distinct().Order();
            foreach (var year in years)
            {
                var sums = mapRows.Select(m => cellList.Where(c => c.Year == year && matches[m.RowLabel].Contains(c.Label))
                    .Select(c => c.Value).ToList()).ToList();
                if (sums.Any(v => v.Count == 0))
                {
                    partials.Add((key.Key.Mode, key.Key.VehicleClass ?? "", year));
                    continue;
                }
                rows.Add(new ExposureRow(key.Key.Mode, year, key.Key.VehicleClass, key.Key.RegulationClass, metric,
                    sums.Sum(v => v.Sum()) * unitMultiplier, mapRows[0].Provenance));
            }
        }
        return (rows, partials);
    }

    /// <summary>The sheet label equals the mapping label, optionally followed by footnote letters glued on
    /// ("Motorcycleb", "...wheel basea,b", "Otherg, h"). Comparison ignores case in the base part only.</summary>
    internal static bool LabelMatches(string sheetLabel, string mapLabel)
    {
        var m = CleanLabel(mapLabel);
        if (!sheetLabel.StartsWith(m, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = sheetLabel[m.Length..];
        return rest.Length == 0 || FootnoteSuffix().IsMatch(rest);
    }

    internal static IReadOnlyList<RowMap> LoadMap(string path)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        csv.Read();
        csv.ReadHeader();
        var rows = new List<RowMap>();
        while (csv.Read())
            rows.Add(new RowMap(csv.GetField("row_label")!.Trim(), csv.GetField("mode")!.Trim(),
                Blank(csv.GetField("vehicle_class")), Blank(csv.GetField("regulation_class")),
                csv.GetField("provenance")!.Trim()));
        return rows;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Trims, collapses whitespace and strips trailing superscript footnote marks.</summary>
    internal static string CleanLabel(string raw) =>
        TrailingSuperscripts().Replace(Whitespace().Replace(raw, " ").Trim(), "").TrimEnd();

    internal static bool TryYear(IXLCell cell, out int year)
    {
        year = 0;
        if (cell.IsEmpty()) return false;
        if (cell.DataType == XLDataType.Number)
        {
            var d = cell.GetDouble();
            if (d != Math.Floor(d)) return false;
            year = (int)d;
        }
        else if (!int.TryParse(cell.GetFormattedString().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out year))
            return false;
        return year is >= 1950 and <= 2100;
    }

    internal static bool TryNumber(IXLCell cell, out double value)
    {
        value = 0;
        if (cell.IsEmpty()) return false;
        if (cell.DataType == XLDataType.Number) { value = cell.GetDouble(); return double.IsFinite(value); }
        return double.TryParse(cell.GetFormattedString().Trim(), NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"[²³¹⁰-₟]+$")] private static partial Regex TrailingSuperscripts();
    [GeneratedRegex(@"^[\s,]*[a-z](\s*,\s*[a-z])*$")] private static partial Regex FootnoteSuffix();
}
