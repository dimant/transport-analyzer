using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Options;
using Npgsql;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;
using TransportAnalyzer.Ingestion;
using TransportAnalyzer.Ingestion.Fars;

namespace TransportAnalyzer.Tests;

/// <summary>Serves the hand-built FARS fixture (Fixtures/fars/{year}) as an in-memory national CSV zip.</summary>
public static class FarsTestSource
{
    public static string FixtureDir(int year) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "fars", year.ToString());

    /// <summary>Builds the zip the way NHTSA ships it: 2020+ zips nest lowercase files in a folder, 2019 has
    /// mixed-case names at the root. The fixture mixes both. <paramref name="edit"/> may rewrite a file's text.</summary>
    public static byte[] BuildZip(int year, Func<string, string, string>? edit = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry($"FARS{year}NationalCSV/");
            foreach (var (file, entryName) in new[]
                     {
                         ("accident.csv", $"FARS{year}NationalCSV/accident.csv"),
                         ("vehicle.csv", $"FARS{year}NationalCSV/vehicle.csv"),
                         ("person.csv", "Person.CSV"),
                     })
            {
                var bytes = File.ReadAllBytes(Path.Combine(FixtureDir(year), file));
                if (edit is not null)
                    bytes = System.Text.Encoding.UTF8.GetBytes(edit(file, System.Text.Encoding.UTF8.GetString(bytes)));
                using var entry = zip.CreateEntry(entryName).Open();
                entry.Write(bytes);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>Deletes fars 2022 events (other FARS tests ingest variants that add crashes) and re-ingests the
    /// clean fixture.</summary>
    public static async Task ResetToCleanFixtureAsync(string connectionString, string rawRoot)
    {
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var del = new NpgsqlCommand(
                "delete from event where source_id = 'fars' and source_key like '2022-%'", conn);
            await del.ExecuteNonQueryAsync();
        }
        await new IngestRunner(connectionString, new RawStore(rawRoot))
            .RunAsync(Adapter(BuildZip(2022)), new IngestParameters([2022]), CancellationToken.None);
    }

    public static FarsAdapter Adapter(byte[] zip, StubHandler? handler = null)
        => new(new HttpClient(handler ?? new StubHandler(zip)), Options.Create(new FarsOptions()));

    public sealed class StubHandler(byte[] body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
