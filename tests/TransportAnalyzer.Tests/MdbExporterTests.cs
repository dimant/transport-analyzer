using TransportAnalyzer.Ingestion.Ntsb;

namespace TransportAnalyzer.Tests;

public class MdbExporterTests
{
    private static bool OnPath(string binary) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => File.Exists(Path.Combine(d, binary)));

    [SkippableFact]
    public async Task MdbToolsExporter_MissingFile_ThrowsWithStderr()
    {
        Skip.IfNot(OnPath("mdb-tables"), "mdbtools is not installed on this host");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MdbToolsExporter().ListTablesAsync("/nonexistent.mdb", CancellationToken.None));
        Assert.Contains("exit code", ex.Message);
        Assert.Contains("/nonexistent.mdb", ex.Message);
    }

    [Fact]
    public async Task MdbToolsExporter_MissingBinary_ThrowsClearError()
    {
        var exporter = new MdbToolsExporter("definitely-not-a-binary");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exporter.ListTablesAsync("x.mdb", CancellationToken.None));
        Assert.Contains("definitely-not-a-binary", ex.Message);
        Assert.Contains("mdbtools", ex.Message);
    }

    [Fact]
    public async Task MdbToolsExporter_MissingExportBinary_ThrowsClearError()
    {
        var exporter = new MdbToolsExporter(mdbExportPath: "definitely-not-a-binary");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exporter.ExportAsync("x.mdb", "T", CancellationToken.None));
        Assert.Contains("mdbtools", ex.Message);
    }
}
