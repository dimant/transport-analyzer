using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;

namespace TransportAnalyzer.Ingestion.Ntsb;

/// <summary>NTSB aviation accident database: avall.zip holds one Access MDB; every table is exported with
/// <see cref="IMdbExporter"/> and staged into <c>stg_ntsb</c>, then normalized into <c>event</c> and one
/// <c>vehicle</c> per aircraft by <c>ntsb-normalize.sql</c>.</summary>
public sealed partial class NtsbAdapter(HttpClient http, IMdbExporter mdb, IOptions<NtsbOptions> o, ILogger<NtsbAdapter>? logger = null)
    : ISourceAdapter
{
    /// <summary>Prefix of the temp directory each staged zip is extracted into.</summary>
    public const string TempDirectoryPrefix = "ta-ntsb-";

    private const string StagingSchema = "stg_ntsb";
    private const string SnapshotColumn = "snapshot";

    public SourceDescriptor Descriptor { get; } = new("ntsb", "NTSB aviation accident and incident database", "aviation", "events",
        "https://data.ntsb.gov/avdata",
        "Accident: an occurrence associated with aircraft operation in which a person is fatally or seriously injured or the aircraft receives substantial damage (49 CFR 830.2). Incident: an occurrence other than an accident that affects or could affect safety.",
        "Only occurrences the NTSB investigated or recorded. avall covers 2008 onwards. Final reports take 12–24 months, so recent years are incomplete and revised. Scheduled Part 135 (commuter) is not covered by FAA survey exposure. " +
        "occurred_on is the UTC date of the occurrence (most events have ev_tmzn UTC), while FARS dates are local, so an evening accident in US time can fall on the next day.");

    private readonly NtsbOptions _options = o.Value;

    /// <summary>Deletes leftover <c>ta-ntsb-*</c> extraction directories under <paramref name="tempRoot"/> (left by a run
    /// that was killed mid-stage). Call at startup, before any run. Failed deletes are logged as warnings.</summary>
    /// <returns>The number of directories deleted.</returns>
    public static int SweepTempDirectories(string tempRoot, ILogger? logger)
    {
        var deleted = 0;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(tempRoot, TempDirectoryPrefix + "*").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not list leftover NTSB temp directories in {TempRoot}", tempRoot);
            return 0;
        }
        foreach (var dir in dirs)
        {
            if (TryDeleteDirectory(dir, logger)) deleted++;
        }
        if (deleted > 0) logger?.LogInformation("Deleted {Count} leftover NTSB temp director(ies) in {TempRoot}", deleted, tempRoot);
        return deleted;
    }

    private static bool TryDeleteDirectory(string dir, ILogger? logger)
    {
        try
        {
            Directory.Delete(dir, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not delete NTSB temp directory {Directory}", dir);
            return false;
        }
    }

    private static readonly Lazy<string> NormalizeSql = new(() =>
    {
        using var s = typeof(NtsbAdapter).Assembly.GetManifestResourceStream("TransportAnalyzer.Ingestion.Ntsb.ntsb-normalize.sql")
            ?? throw new InvalidOperationException("Embedded resource ntsb-normalize.sql is missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    [GeneratedRegex("[^a-z0-9_]")]
    private static partial Regex NonIdentifierChars();

    /// <summary>Requested years, or null for every year in the snapshot.</summary>
    private static int[]? Years(IngestParameters p) =>
        p.Years is { Count: > 0 } ? p.Years.Distinct().Order().ToArray() : null;

    public async Task<IReadOnlyList<RawFile>> FetchAsync(IRawStore raw, IngestParameters p, CancellationToken ct)
    {
        using var res = await http.GetAsync(_options.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using var body = await res.Content.ReadAsStreamAsync(ct);
        return [await raw.SaveAsync(Descriptor.SourceId, $"{_options.Snapshot}.zip", body, ct)];
    }

    /// <summary>Extracts the zip's single MDB to a temp directory and stages every non-system table into
    /// <c>stg_ntsb</c>, replacing the rows of this snapshot (avall is a full reload).</summary>
    public async Task StageAsync(IReadOnlyList<RawFile> files, NpgsqlConnection conn, IngestParameters p, CancellationToken ct)
    {
        var extra = new Dictionary<string, string> { [SnapshotColumn] = _options.Snapshot };
        foreach (var file in files)
        {
            var dir = Path.Combine(Path.GetTempPath(), TempDirectoryPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var mdbPath = Path.Combine(dir, "source.mdb"); // own name: never a path taken from the zip
                await using (var zipStream = File.OpenRead(file.Path))
                using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    var entries = zip.Entries.Where(e => e.Name.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (entries.Count != 1)
                        throw new InvalidDataException($"{file.Name} holds {entries.Count} .mdb files; expected exactly one.");
                    await using var src = entries[0].Open();
                    await using var dst = File.Create(mdbPath);
                    await src.CopyToAsync(dst, ct);
                }

                var staged = new HashSet<string>();
                foreach (var table in await mdb.ListTablesAsync(mdbPath, ct))
                {
                    if (table.StartsWith("MSys", StringComparison.Ordinal)) continue;
                    var name = NonIdentifierChars().Replace(table.ToLowerInvariant(), "_");
                    if (!staged.Add(name))
                        throw new InvalidDataException($"{file.Name}: more than one MDB table maps to staging table '{name}'.");
                    await using var csv = await mdb.ExportAsync(mdbPath, table, ct);
                    await CsvStager.StageAsync(conn, StagingSchema, name, csv, extra, SnapshotColumn, ct);
                }
            }
            finally
            {
                TryDeleteDirectory(dir, logger); // a leftover is removed by the next startup's sweep
            }
        }
    }

    public async Task<NormalizeResult> NormalizeAsync(NpgsqlConnection conn, Guid runId, IngestParameters p, CancellationToken ct)
    {
        var years = Years(p);
        await EnsureYearsHaveEventsAsync(conn, years, ct);
        await using var cmd = new NpgsqlCommand(NormalizeSql.Value, conn) { CommandTimeout = 1800 };
        cmd.Parameters.Add(new NpgsqlParameter("years", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = (object?)years ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("snapshot", NpgsqlDbType.Text) { Value = _options.Snapshot });
        cmd.Parameters.Add(new NpgsqlParameter("run_id", NpgsqlDbType.Uuid) { Value = runId });

        var rowCounts = new Dictionary<string, long>();
        var unmapped = new Dictionary<string, long>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        do
        {
            while (await r.ReadAsync(ct))
                (r.GetString(0) == "rows" ? rowCounts : unmapped)[r.GetString(1)] = r.GetInt64(2);
        } while (await r.NextResultAsync(ct));
        return new NormalizeResult(rowCounts, unmapped);
    }

    /// <summary>A requested year with no staged events would make the normalize delete all of that year's events; an
    /// empty snapshot would do the same for every year. Fail the run instead so the transaction rolls back.</summary>
    private async Task EnsureYearsHaveEventsAsync(NpgsqlConnection conn, int[]? years, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(years is null
            ? "select null::int from (select 1) x where not exists (select 1 from stg_ntsb.events where snapshot = @snapshot)"
            : """
              select y from unnest(@years) y
              where not exists (select 1 from stg_ntsb.events e where e.snapshot = @snapshot and nullif(trim(e.ev_year), '')::int = y)
              order by y
              """, conn);
        cmd.Parameters.Add(new NpgsqlParameter("snapshot", NpgsqlDbType.Text) { Value = _options.Snapshot });
        if (years is not null)
            cmd.Parameters.Add(new NpgsqlParameter("years", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = years });
        var empty = new List<int?>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) empty.Add(r.IsDBNull(0) ? null : r.GetInt32(0));
        if (empty.Count == 0) return;
        throw new InvalidDataException(years is null
            ? $"NTSB snapshot '{_options.Snapshot}' has no event rows; refusing to replace existing events."
            : $"NTSB year(s) {string.Join(", ", empty)} have no event rows in snapshot '{_options.Snapshot}'; refusing to replace existing events.");
    }
}
