using System.Data.Common;
using System.Globalization;
using System.Text;
using Dapper;

namespace TransportAnalyzer.Api.Query;

/// <summary>A validated aggregate over events or vehicles. Build with <see cref="Parse"/> (HTTP query string) or
/// <see cref="Create"/> (code); both validate identically. SQL text comes only from <see cref="Dimensions"/> and
/// fixed fragments; every request value is a bound parameter.</summary>
public sealed class AggregateQuery
{
    public const string PeriodColumn = "period";

    private static readonly (string Name, string Sql)[] EventMeasures =
    [
        ("events", "count(*)"),
        ("fatalities", "coalesce(sum(e.fatalities), 0)"),
        ("serious_injuries", "coalesce(sum(e.serious_injuries), 0)"),
        ("minor_injuries", "coalesce(sum(e.minor_injuries), 0)"),
        ("unattributed_fatalities", "coalesce(sum(e.unattributed_fatalities), 0)"),
    ];

    private static readonly (string Name, string Sql)[] VehicleMeasures =
    [
        ("vehicles", "count(*)"),
        ("events", "count(distinct v.event_id)"),
        ("occupant_fatalities", "coalesce(sum(v.occupant_fatalities), 0)"),
        ("occupant_serious_injuries", "coalesce(sum(v.occupant_serious_injuries), 0)"),
        ("occupant_minor_injuries", "coalesce(sum(v.occupant_minor_injuries), 0)"),
        ("nonoccupant_fatalities", "coalesce(sum(v.nonoccupant_fatalities), 0)"),
        ("nonoccupant_serious_injuries", "coalesce(sum(v.nonoccupant_serious_injuries), 0)"),
        ("nonoccupant_minor_injuries", "coalesce(sum(v.nonoccupant_minor_injuries), 0)"),
        ("vehicles_with_occupant_fatality", "count(*) filter (where v.occupant_fatalities > 0)"),
    ];

    private static readonly Dictionary<string, Grain> Grains = new(StringComparer.Ordinal)
    {
        ["event"] = Grain.Event,
        ["vehicle"] = Grain.Vehicle,
    };

    private static readonly Dictionary<string, string?> Buckets = new(StringComparer.Ordinal)
    {
        ["none"] = null,
        ["year"] = "to_char(e.occurred_on, 'YYYY')",
        ["quarter"] = "to_char(e.occurred_on, 'YYYY-\"Q\"Q')",
        ["month"] = "to_char(e.occurred_on, 'YYYY-MM')",
    };

    private readonly string? _bucketSql;
    private readonly IReadOnlyList<(Dimension Dimension, Array Values)> _filters;

    private AggregateQuery(Grain grain, DateOnly from, DateOnly to, string? bucketSql, IReadOnlyList<Dimension> groupBy,
        IReadOnlyList<(Dimension Dimension, Array Values)> filters)
    {
        Grain = grain;
        From = from;
        To = to;
        _bucketSql = bucketSql;
        GroupBy = groupBy;
        _filters = filters;
    }

    public Grain Grain { get; }
    public DateOnly From { get; }
    public DateOnly To { get; }
    public IReadOnlyList<Dimension> GroupBy { get; }

    /// <summary>Response column names: group columns in request order, then <c>period</c> if bucketed, then measures.</summary>
    public IReadOnlyList<string> Columns =>
        GroupBy.Select(d => d.Name)
            .Concat(_bucketSql is null ? [] : [PeriodColumn])
            .Concat(Measures.Select(m => m.Name))
            .ToList();

    private (string Name, string Sql)[] Measures => Grain == Grain.Event ? EventMeasures : VehicleMeasures;

    /// <summary>Parses <c>grain, from, to, bucket, groupBy, f.{dimension}</c>. Other parameters are ignored.</summary>
    public static Result<AggregateQuery, (string error, string parameter)> Parse(IQueryCollection q)
    {
        var scope = ParseScope(q);
        if (!scope.IsSuccess) return Result<AggregateQuery, (string error, string parameter)>.Fail(scope.Error);
        var (from, to, filters) = scope.Value;

        var grain = q["grain"].ToString();
        var bucket = q["bucket"].ToString();
        var groupBy = q["groupBy"].ToString()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return Create(grain.Length == 0 ? "event" : grain, from, to, bucket.Length == 0 ? "none" : bucket, groupBy, filters);
    }

    /// <summary>Parses the parts every query shares: <c>from</c>, <c>to</c> and the <c>f.{dimension}</c> filters
    /// (unvalidated names; <see cref="Create"/> validates them).</summary>
    internal static Result<(DateOnly From, DateOnly To, Dictionary<string, IReadOnlyList<string>> Filters), (string error, string parameter)>
        ParseScope(IQueryCollection q)
    {
        if (!TryParseDate(q["from"].ToString(), out var from))
            return Result<(DateOnly From, DateOnly To, Dictionary<string, IReadOnlyList<string>> Filters), (string error, string parameter)>.Fail(("'from' must be an ISO date (yyyy-MM-dd)", "from"));
        if (!TryParseDate(q["to"].ToString(), out var to))
            return Result<(DateOnly From, DateOnly To, Dictionary<string, IReadOnlyList<string>> Filters), (string error, string parameter)>.Fail(("'to' must be an ISO date (yyyy-MM-dd)", "to"));
        var filters = q.Keys
            .Where(k => k.StartsWith("f.", StringComparison.Ordinal))
            .ToDictionary(k => k[2..], k => (IReadOnlyList<string>)q[k]
                .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .ToList());
        return (from, to, filters);
    }

    /// <summary>Builds a query in code. <paramref name="grain"/> is <c>event</c> or <c>vehicle</c>,
    /// <paramref name="bucket"/> is <c>none</c>, <c>year</c>, <c>quarter</c> or <c>month</c>, filter keys are
    /// dimension names (without the <c>f.</c> prefix). Errors name the HTTP parameter at fault.</summary>
    public static Result<AggregateQuery, (string error, string parameter)> Create(string grain, DateOnly from, DateOnly to,
        string bucket, IReadOnlyList<string> groupBy, IReadOnlyDictionary<string, IReadOnlyList<string>> filters)
    {
        if (!Grains.TryGetValue(grain, out var g))
            return Fail($"unknown grain '{grain}' (expected event or vehicle)", "grain");
        if (from > to)
            return Fail("'from' must not be after 'to'", "from");
        if (!Buckets.TryGetValue(bucket, out var bucketSql))
            return Fail($"unknown bucket '{bucket}' (expected none, year, quarter or month)", "bucket");

        var groups = new List<Dimension>();
        foreach (var name in groupBy)
        {
            if (Resolve(name, g, "groupBy") is { } error) return error;
            var dim = Dimensions.All[name];
            if (groups.Contains(dim)) return Fail($"dimension '{name}' appears more than once", "groupBy");
            groups.Add(dim);
        }

        var parsedFilters = new List<(Dimension, Array)>();
        foreach (var (name, values) in filters)
        {
            var parameter = "f." + name;
            if (Resolve(name, g, parameter) is { } error) return error;
            if (values.Count == 0) return Fail($"filter '{parameter}' needs at least one value", parameter);
            var dim = Dimensions.All[name];
            if (!dim.IsInteger)
            {
                parsedFilters.Add((dim, values.ToArray()));
                continue;
            }
            var ints = new int[values.Count];
            for (var i = 0; i < values.Count; i++)
                if (!int.TryParse(values[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ints[i]))
                    return Fail($"filter '{parameter}' takes integers", parameter);
            parsedFilters.Add((dim, ints));
        }

        return new AggregateQuery(g, from, to, bucketSql, groups, parsedFilters);
    }

    private static Result<AggregateQuery, (string error, string parameter)>? Resolve(string name, Grain grain, string parameter)
    {
        if (!Dimensions.All.TryGetValue(name, out var dim))
            return Fail($"unknown dimension '{name}'", parameter);
        if (dim.Grain == Grain.Vehicle && grain == Grain.Event)
            return Fail($"dimension '{name}' needs grain=vehicle", parameter);
        return null;
    }

    private static Result<AggregateQuery, (string error, string parameter)> Fail(string error, string parameter) =>
        Result<AggregateQuery, (string error, string parameter)>.Fail((error, parameter));

    private static bool TryParseDate(string s, out DateOnly date) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>The <c>from … where …</c> fragment shared with <see cref="SampleQuery"/>; adds its bound parameters
    /// (<c>from</c>, <c>to</c>, <c>f_*</c>) to <paramref name="p"/>.</summary>
    internal string FromWhere(DynamicParameters p)
    {
        p.Add("from", From);
        p.Add("to", To);
        var sql = new StringBuilder(Grain == Grain.Event
            ? " from event e"
            : " from vehicle v join event e on e.event_id = v.event_id" +
              " left join vocabulary vc on vc.attribute = 'vehicle_class' and vc.code = v.vehicle_class");
        sql.Append(" where e.occurred_on between @from and @to");
        foreach (var (dim, values) in _filters)
        {
            sql.Append($" and {dim.Sql} = any(@f_{dim.Name})");
            p.Add("f_" + dim.Name, values);
        }
        return sql.ToString();
    }

    public (string sql, DynamicParameters p) ToSql()
    {
        var p = new DynamicParameters();

        var select = new List<string>();
        select.AddRange(GroupBy.Select(d => $"{d.Sql} as \"{d.Name}\""));
        if (_bucketSql is not null) select.Add($"{_bucketSql} as \"{PeriodColumn}\"");
        var keyCount = select.Count;
        select.AddRange(Measures.Select(m => $"{m.Sql} as \"{m.Name}\""));

        var sql = new StringBuilder("select ").AppendJoin(", ", select);
        sql.Append(FromWhere(p));
        if (keyCount > 0)
        {
            var ordinals = string.Join(", ", Enumerable.Range(1, keyCount));
            sql.Append(" group by ").Append(ordinals).Append(" order by ").Append(ordinals);
        }
        return (sql.ToString(), p);
    }

    /// <summary>Runs the query and returns the response table. Null dimension values stay a group of their own.</summary>
    public async Task<AggregateResult> RunAsync(DbConnection conn, CancellationToken ct)
    {
        var (sql, p) = ToSql();
        await using var reader = await conn.ExecuteReaderAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return new AggregateResult(Grain == Grain.Event ? "event" : "vehicle", Columns, rows);
    }
}

/// <summary>The aggregate response: <c>{ grain, columns, rows }</c>. Measures are <see cref="long"/>; integer
/// dimensions <see cref="int"/>; text dimensions and <c>period</c> <see cref="string"/>; any dimension may be null.</summary>
public sealed record AggregateResult(string Grain, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);
