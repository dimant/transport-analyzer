using System.Globalization;
using Dapper;
using Npgsql;
using TransportAnalyzer.Api.Query;
using TransportAnalyzer.Domain;

namespace TransportAnalyzer.Api.Endpoints;

/// <summary><c>/api/exposure</c> lists exposure rows; <c>/api/rates</c> pairs casualty numerators with exposure
/// denominators through <see cref="RateJoiner"/>. No rate is computed here.</summary>
public static class ExposureEndpoints
{
    private static readonly string[] Metrics = ["passenger_miles", "vehicle_miles", "flight_hours"];

    /// <summary>Exposure source used when <c>exposureSource</c> is not given; metrics absent here need one.</summary>
    private static readonly Dictionary<string, string> DefaultSources = new(StringComparer.Ordinal)
    {
        ["passenger_miles"] = "bts-passenger-miles",
        ["vehicle_miles"] = "bts-vehicle-miles",
    };

    /// <summary>Vehicle-grain measures summed into each numerator. Attributed casualties only.</summary>
    private static readonly Dictionary<string, string[]> Measures = new(StringComparer.Ordinal)
    {
        ["occupant_fatalities"] = ["occupant_fatalities"],
        ["societal_fatalities"] = ["occupant_fatalities", "nonoccupant_fatalities"],
    };

    private const string VehicleClass = "vehicle_class";
    private const string RegulationClass = "regulation_class";

    private static readonly string[] RateColumns =
        ["mode", "year", VehicleClass, RegulationClass, "numerator", "denominator", "join_level", "exposure_source", "provenance"];

    private static readonly string[] ExposureColumns =
        ["source_id", "mode", "year", VehicleClass, RegulationClass, "metric", "value", "provenance", "standard_error"];

    private static readonly Dictionary<string, IReadOnlyList<string>> NoFilters = new();

    public static void MapExposureEndpoints(this WebApplication app, string connectionString)
    {
        app.MapGet("/api/exposure", async (HttpRequest request, CancellationToken ct) =>
        {
            var q = request.Query;
            var metric = q["metric"].ToString();
            if (!Metrics.Contains(metric)) return BadRequest(UnknownMetric(metric), "metric");
            if (!TryParseYear(q, "fromYear", required: false, out var fromYear, out var fromError)) return fromError!;
            if (!TryParseYear(q, "toYear", required: false, out var toYear, out var toError)) return toError!;

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            var rows = await conn.QueryAsync<ExposureRecord>(new CommandDefinition("""
                select source_id as SourceId, mode as Mode, year as Year, vehicle_class as VehicleClass,
                       regulation_class as RegulationClass, metric as Metric, value as Value, provenance as Provenance,
                       standard_error as StandardError
                from exposure
                where metric = @metric
                  and (@mode::text is null or mode = @mode)
                  and (@fromYear::int is null or year >= @fromYear)
                  and (@toYear::int is null or year <= @toYear)
                  and (@vehicleClass::text is null or vehicle_class = @vehicleClass)
                  and (@regulationClass::text is null or regulation_class = @regulationClass)
                  and (@sourceId::text is null or source_id = @sourceId)
                order by source_id, mode, year, vehicle_class nulls first, regulation_class nulls first
                """, new
            {
                metric,
                mode = Optional(q, "mode"),
                fromYear,
                toYear,
                vehicleClass = Optional(q, "vehicleClass"),
                regulationClass = Optional(q, "regulationClass"),
                sourceId = Optional(q, "sourceId"),
            }, cancellationToken: ct));

            return Results.Ok(new
            {
                columns = ExposureColumns,
                rows = rows.Select(r => new object?[]
                {
                    r.SourceId, r.Mode, r.Year, r.VehicleClass, r.RegulationClass, r.Metric, r.Value, r.Provenance,
                    r.StandardError,
                }),
            });
        });

        app.MapGet("/api/rates", async (HttpRequest request, IEnumerable<ISourceAdapter> adapters, CancellationToken ct) =>
        {
            var q = request.Query;
            var measure = q["measure"].ToString();
            if (!Measures.TryGetValue(measure, out var measureColumns))
                return BadRequest($"unknown measure '{measure}' (expected {string.Join(" or ", Measures.Keys)})", "measure");
            var metric = q["metric"].ToString();
            if (!Metrics.Contains(metric)) return BadRequest(UnknownMetric(metric), "metric");
            if (!TryParseYear(q, "fromYear", required: true, out var fromYear, out var fromError)) return fromError!;
            if (!TryParseYear(q, "toYear", required: true, out var toYear, out var toError)) return toError!;
            if (fromYear > toYear) return BadRequest("'fromYear' must not be after 'toYear'", "fromYear");

            var groupBy = q["groupBy"].ToString()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var g in groupBy)
                if (g is not (VehicleClass or RegulationClass))
                    return BadRequest($"cannot group rates by '{g}' (expected {VehicleClass} and/or {RegulationClass})", "groupBy");
            if (groupBy.Distinct().Count() != groupBy.Length)
                return BadRequest("groupBy lists a dimension more than once", "groupBy");

            var exposureSource = Optional(q, "exposureSource") ?? DefaultSources.GetValueOrDefault(metric);
            if (exposureSource is null)
                return BadRequest($"metric '{metric}' has no default exposure source; pass exposureSource", "exposureSource");
            if (!adapters.Any(a => a.Descriptor.SourceId == exposureSource && a.Descriptor.Kind == "exposure"))
                return BadRequest($"'{exposureSource}' is not a registered exposure source", "exposureSource");

            var from = new DateOnly(fromYear!.Value, 1, 1);
            var to = new DateOnly(toYear!.Value, 12, 31);

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);

            var numerators = await NumeratorsAsync(conn, from, to, measureColumns, ct);
            var exposure = await conn.QueryAsync<ExposureRecord>(new CommandDefinition("""
                select source_id as SourceId, mode as Mode, year as Year, vehicle_class as VehicleClass,
                       regulation_class as RegulationClass, metric as Metric, value as Value, provenance as Provenance,
                       standard_error as StandardError
                from exposure
                where source_id = @exposureSource and metric = @metric and year between @fromYear and @toYear
                """, new { exposureSource, metric, fromYear, toYear }, cancellationToken: ct));
            var parents = (await conn.QueryAsync<(string Code, string? ParentCode)>(new CommandDefinition(
                    "select code, parent_code from vocabulary where attribute = 'vehicle_class'", cancellationToken: ct)))
                .ToDictionary(p => p.Code, p => p.ParentCode);

            var result = RateJoiner.Join(numerators,
                exposure.Select(e => new ExposureCell(new RateKey(e.Mode, e.Year, e.VehicleClass, e.RegulationClass),
                    e.Value, e.SourceId, e.Provenance)),
                parents, groupBy.Contains(VehicleClass), groupBy.Contains(RegulationClass));

            return Results.Ok(new
            {
                measure,
                metric,
                columns = RateColumns,
                rows = result.Rows.Select(r => new object?[]
                {
                    r.Key.Mode, r.Key.Year, r.Key.VehicleClass, r.Key.RegulationClass, r.Numerator, r.Denominator,
                    r.JoinLevel, r.ExposureSource, r.Provenance,
                }),
                unmatched = result.Unmatched.Select(u => new
                {
                    mode = u.Key.Mode,
                    year = u.Key.Year,
                    vehicleClass = u.Key.VehicleClass,
                    regulationClass = u.Key.RegulationClass,
                    numerator = u.Value,
                }),
                unattributed = await UnattributedAsync(conn, from, to, ct),
            });
        });
    }

    /// <summary>Numerator cells at (mode, year, vehicle_class, regulation_class) from the aggregate query builder.</summary>
    private static async Task<List<NumeratorCell>> NumeratorsAsync(NpgsqlConnection conn, DateOnly from, DateOnly to,
        string[] measureColumns, CancellationToken ct)
    {
        var table = await Build("vehicle", from, to, ["mode", VehicleClass, RegulationClass]).RunAsync(conn, ct);
        var columns = table.Columns.ToList();
        int mode = columns.IndexOf("mode"), period = columns.IndexOf(AggregateQuery.PeriodColumn),
            vehicleClass = columns.IndexOf(VehicleClass), regulationClass = columns.IndexOf(RegulationClass);
        var measureIndexes = measureColumns.Select(c => columns.IndexOf(c)).ToArray();
        return table.Rows.Select(r => new NumeratorCell(
                new RateKey((string)r[mode]!, ParseYear(r[period]), (string?)r[vehicleClass], (string?)r[regulationClass]),
                measureIndexes.Sum(i => (long)r[i]!)))
            .ToList();
    }

    /// <summary>Event-grain unattributed fatalities per (mode, year); rows with none are left out.</summary>
    private static async Task<IEnumerable<object>> UnattributedAsync(NpgsqlConnection conn, DateOnly from, DateOnly to,
        CancellationToken ct)
    {
        var table = await Build("event", from, to, ["mode"]).RunAsync(conn, ct);
        var columns = table.Columns.ToList();
        var period = columns.IndexOf(AggregateQuery.PeriodColumn);
        var fatalities = columns.IndexOf("unattributed_fatalities");
        return table.Rows
            .Where(r => (long)r[fatalities]! != 0)
            .Select(r => new { mode = (string)r[columns.IndexOf("mode")]!, year = ParseYear(r[period]), fatalities = (long)r[fatalities]! })
            .ToList();
    }

    private static AggregateQuery Build(string grain, DateOnly from, DateOnly to, IReadOnlyList<string> groupBy)
    {
        var query = AggregateQuery.Create(grain, from, to, "year", groupBy, NoFilters);
        return query.IsSuccess ? query.Value
            : throw new InvalidOperationException($"rates aggregate rejected: {query.Error.error}");
    }

    private static int ParseYear(object? period) => int.Parse((string)period!, CultureInfo.InvariantCulture);

    private static string UnknownMetric(string metric) =>
        $"unknown metric '{metric}' (expected {string.Join(", ", Metrics)})";

    private static string? Optional(IQueryCollection q, string name) =>
        q[name].ToString() is { Length: > 0 } value ? value : null;

    private static bool TryParseYear(IQueryCollection q, string name, bool required, out int? year, out IResult? error)
    {
        year = null;
        error = null;
        var text = q[name].ToString();
        if (text.Length == 0 && !required) return true;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1 and <= 9999)
        {
            year = y;
            return true;
        }
        error = BadRequest($"'{name}' must be a year (integer)", name);
        return false;
    }

    private static IResult BadRequest(string error, string parameter) => Results.BadRequest(new { error, parameter });

    private sealed record ExposureRecord(string SourceId, string Mode, int Year, string? VehicleClass,
        string? RegulationClass, string Metric, double Value, string Provenance, double? StandardError);
}
