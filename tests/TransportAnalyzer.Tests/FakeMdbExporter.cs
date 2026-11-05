using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;
using TransportAnalyzer.Ingestion.Ntsb;

namespace TransportAnalyzer.Tests;

/// <summary>Serves the hand-built NTSB fixture (Fixtures/ntsb/*.csv) as MDB tables, ignoring the MDB path. Lists an
/// extra <c>MSysObjects</c> system table, which the adapter must skip. <paramref name="edit"/> may rewrite a table's
/// text (table name, text).</summary>
public sealed class FakeMdbExporter(Func<string, string, string>? edit = null) : IMdbExporter
{
    public const string SystemTable = "MSysObjects";

    public static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ntsb");

    /// <summary>MDB paths seen by the calls, with whether the file existed at call time.</summary>
    public List<(string Path, bool Existed)> Calls { get; } = [];

    public List<string> Exported { get; } = [];

    public Task<IReadOnlyList<string>> ListTablesAsync(string mdbPath, CancellationToken ct)
    {
        Calls.Add((mdbPath, File.Exists(mdbPath)));
        var tables = Directory.GetFiles(FixtureDir, "*.csv").Select(Path.GetFileNameWithoutExtension).Order().ToList();
        return Task.FromResult<IReadOnlyList<string>>([SystemTable, .. tables!]);
    }

    public Task<Stream> ExportAsync(string mdbPath, string table, CancellationToken ct)
    {
        Calls.Add((mdbPath, File.Exists(mdbPath)));
        if (table == SystemTable) throw new InvalidOperationException("system tables must not be exported");
        Exported.Add(table);
        var text = File.ReadAllText(Path.Combine(FixtureDir, table + ".csv"));
        if (edit is not null) text = edit(table, text);
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>NTSB adapter wiring for tests: a stub HTTP handler serving a zip with a dummy avall.mdb, and the fake
/// exporter serving the fixture tables.</summary>
public static class NtsbTestSource
{
    public static byte[] BuildZip()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("avall.mdb").Open();
            entry.Write("not a real mdb"u8);
        }
        return buffer.ToArray();
    }

    public static NtsbAdapter Adapter(FakeMdbExporter? mdb = null, FarsTestSource.StubHandler? handler = null) =>
        new(new HttpClient(handler ?? new FarsTestSource.StubHandler(BuildZip())), mdb ?? new FakeMdbExporter(),
            Options.Create(new NtsbOptions()));
}
