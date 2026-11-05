using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;

namespace TransportAnalyzer.Ingestion.Fars;

/// <summary>NHTSA FARS: one national CSV zip per year, every table staged into <c>stg_fars</c>, then normalized
/// into <c>event</c> and <c>vehicle</c> by <c>fars-normalize.sql</c>.</summary>
public sealed partial class FarsAdapter(HttpClient http, IOptions<FarsOptions> o) : ISourceAdapter
{
    private const string StagingSchema = "stg_fars";

    public SourceDescriptor Descriptor { get; } = new("fars", "NHTSA Fatality Analysis Reporting System", "highway", "events",
        "https://www.nhtsa.gov/research-data/fatality-analysis-reporting-system-fars",
        "Fatal crash: a crash on a public trafficway involving a motor vehicle in transport that results in a death within 30 days.",
        "Census of fatal crashes only. Non-fatal crashes are not included. Annual release with a lag of about 18 months; preliminary years are revised.");

    private readonly FarsOptions _options = o.Value;

    private static readonly Lazy<string> NormalizeSql = new(() =>
    {
        using var s = typeof(FarsAdapter).Assembly.GetManifestResourceStream("TransportAnalyzer.Ingestion.Fars.fars-normalize.sql")
            ?? throw new InvalidOperationException("Embedded resource fars-normalize.sql is missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    [GeneratedRegex(@"^FARS(\d{4})NationalCSV\.zip$")]
    private static partial Regex RawFileName();

    private IReadOnlyList<int> Years(IngestParameters p) =>
        (p.Years is { Count: > 0 } ? p.Years : _options.DefaultYears).Distinct().Order().ToList();

    public async Task<IReadOnlyList<RawFile>> FetchAsync(IRawStore raw, IngestParameters p, CancellationToken ct)
    {
        var files = new List<RawFile>();
        foreach (var year in Years(p))
        {
            var y = year.ToString(CultureInfo.InvariantCulture);
            using var res = await http.GetAsync(_options.UrlTemplate.Replace("{year}", y), HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            await using var body = await res.Content.ReadAsStreamAsync(ct);
            files.Add(await raw.SaveAsync(Descriptor.SourceId, $"FARS{y}NationalCSV.zip", body, ct));
        }
        return files;
    }

    public async Task StageAsync(IReadOnlyList<RawFile> files, NpgsqlConnection conn, IngestParameters p, CancellationToken ct)
    {
        foreach (var file in files)
        {
            var match = RawFileName().Match(file.Name);
            if (!match.Success) throw new InvalidDataException($"Unexpected FARS raw file name '{file.Name}'.");
            var extra = new Dictionary<string, string> { ["data_year"] = match.Groups[1].Value };

            await using var zipStream = File.OpenRead(file.Path);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var tables = new HashSet<string>();
            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                var table = Path.GetFileNameWithoutExtension(entry.Name).ToLowerInvariant();
                if (!tables.Add(table))
                    throw new InvalidDataException($"{file.Name} holds more than one CSV for table '{table}'.");
                await using var csv = entry.Open();
                await CsvStager.StageAsync(conn, StagingSchema, table, csv, extra, "data_year", ct);
            }
        }
    }

    public async Task<NormalizeResult> NormalizeAsync(NpgsqlConnection conn, Guid runId, IngestParameters p, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(NormalizeSql.Value, conn) { CommandTimeout = 1800 };
        cmd.Parameters.Add(new NpgsqlParameter("years", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = Years(p).ToArray() });
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
}
