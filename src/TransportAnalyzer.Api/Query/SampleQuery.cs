using System.Data.Common;
using System.Globalization;
using Dapper;

namespace TransportAnalyzer.Api.Query;

/// <summary>A reproducible sample of vehicle rows (each joined to its event). Scope (dates, filters) is parsed and
/// validated by <see cref="AggregateQuery"/>; SQL text comes only from <see cref="Dimensions"/> and fixed fragments.</summary>
public sealed class SampleQuery
{
    public const int DefaultN = 10_000;
    public const int MaxN = 100_000;

    private static readonly string[] Measures =
    [
        "occupant_fatalities", "occupant_serious_injuries", "occupant_minor_injuries",
        "nonoccupant_fatalities", "nonoccupant_serious_injuries", "nonoccupant_minor_injuries",
    ];

    private readonly AggregateQuery _scope;

    private SampleQuery(AggregateQuery scope, int n, long seed) => (_scope, N, Seed) = (scope, n, seed);

    public int N { get; }
    public long Seed { get; }

    public static IReadOnlyList<string> Columns { get; } =
        [.. Dimensions.All.Keys, "occurred_on", .. Measures];

    /// <summary>Parses <c>from, to, n, seed, f.{dimension}</c>.</summary>
    public static Result<SampleQuery, (string error, string parameter)> Parse(IQueryCollection q)
    {
        static Result<SampleQuery, (string error, string parameter)> Fail(string error, string parameter) =>
            Result<SampleQuery, (string error, string parameter)>.Fail((error, parameter));

        var scope = AggregateQuery.ParseScope(q);
        if (!scope.IsSuccess) return Result<SampleQuery, (string error, string parameter)>.Fail(scope.Error);
        var (from, to, filters) = scope.Value;
        var aggregate = AggregateQuery.Create("vehicle", from, to, "none", [], filters);
        if (!aggregate.IsSuccess) return Result<SampleQuery, (string error, string parameter)>.Fail(aggregate.Error);

        var n = DefaultN;
        var nText = q["n"].ToString();
        if (nText.Length > 0 && !int.TryParse(nText, NumberStyles.None, CultureInfo.InvariantCulture, out n))
            return Fail("'n' must be an integer", "n");
        if (n < 1 || n > MaxN) return Fail($"'n' must be between 1 and {MaxN}", "n");

        long seed = 0;
        var seedText = q["seed"].ToString();
        if (seedText.Length > 0 && !long.TryParse(seedText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seed))
            return Fail("'seed' must be an integer", "seed");

        return new SampleQuery(aggregate.Value, n, seed);
    }

    public async Task<SampleResult> RunAsync(DbConnection conn, CancellationToken ct)
    {
        var p = new DynamicParameters();
        var fromWhere = _scope.FromWhere(p);
        p.Add("seed", Seed);
        p.Add("n", N);

        var select = Dimensions.All.Values.Select(d => $"{d.Sql} as \"{d.Name}\"")
            .Append("to_char(e.occurred_on, 'YYYY-MM-DD') as \"occurred_on\"")
            .Concat(Measures.Select(m => $"v.{m} as \"{m}\""));
        var sql = $"select {string.Join(", ", select)}{fromWhere}" +
                  " order by md5(v.vehicle_id::text || @seed::text), v.vehicle_id limit @n";

        var total = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition($"select count(*){fromWhere}", p, cancellationToken: ct));

        await using var reader = await conn.ExecuteReaderAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return new SampleResult(Columns, rows, total, rows.Count, Seed);
    }
}

/// <summary>The sample response: <c>{ columns, rows, total, n, seed }</c>; <c>n</c> is the rows returned.</summary>
public sealed record SampleResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, long Total, int N, long Seed);
