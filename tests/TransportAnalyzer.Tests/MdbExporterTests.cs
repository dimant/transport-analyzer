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

    // Fake mdb-export scripts: the real streaming behaviour without needing mdbtools.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string WriteScript(string dir, string body)
    {
        var path = Path.Combine(dir, "fake-export.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task WithScript(string body, Func<string, string, Task> test)
    {
        Skip.If(OperatingSystem.IsWindows(), "needs a POSIX shell");
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("mdbexport").FullName;
        try { await test(WriteScript(dir, body), dir); }
        finally { Directory.Delete(dir, true); }
    }

    [SkippableFact]
    public Task Export_ScriptSuccess_ContentReadIntact() =>
        WithScript("echo 'a,b'; echo '\"x\ny\",2'", async (script, _) =>
        {
            await using var s = await new MdbToolsExporter(mdbExportPath: script).ExportAsync("f.mdb", "T", CancellationToken.None);
            using var r = new StreamReader(s);
            Assert.Equal("a,b\n\"x\ny\",2\n", await r.ReadToEndAsync());
        });

    [SkippableFact]
    public Task Export_NonZeroExit_ThrowsAtEndOfStreamWithStderr() =>
        WithScript("echo 'a,b'; echo 'boom happened' >&2; exit 3", async (script, _) =>
        {
            await using var s = await new MdbToolsExporter(mdbExportPath: script).ExportAsync("f.mdb", "T", CancellationToken.None);
            var buf = new byte[4];
            Assert.Equal(4, await s.ReadAsync(buf)); // rows arrive without any exception
            var rest = new MemoryStream();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.CopyToAsync(rest));
            Assert.Contains("3", ex.Message);
            Assert.Contains("boom happened", ex.Message);
        });

    [SkippableFact]
    public Task Export_LargeStderr_DoesNotDeadlock() =>
        WithScript("head -c 300000 /dev/zero | tr '\\0' e >&2; echo 'a,b'", async (script, _) =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var s = await new MdbToolsExporter(mdbExportPath: script).ExportAsync("f.mdb", "T", cts.Token);
            using var r = new StreamReader(s);
            Assert.Equal("a,b\n", await r.ReadToEndAsync(cts.Token));
        });

    [SkippableFact]
    public Task Export_DisposeEarly_KillsProcess() =>
        WithScript("echo $$ > \"$0.pid\"; exec sleep 60", async (script, _) =>
        {
            var s = await new MdbToolsExporter(mdbExportPath: script).ExportAsync("f.mdb", "T", CancellationToken.None);
            var pidFile = script + ".pid";
            for (var i = 0; i < 100 && !(File.Exists(pidFile) && File.ReadAllText(pidFile).EndsWith('\n')); i++) await Task.Delay(50);
            var pid = File.ReadAllText(pidFile).Trim();
            Assert.True(IsAlive(pid));
            await s.DisposeAsync();
            for (var i = 0; i < 100 && IsAlive(pid); i++) await Task.Delay(50);
            Assert.False(IsAlive(pid));
        });

    private static bool IsAlive(string pid)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("kill") { ArgumentList = { "-0", pid }, RedirectStandardError = true })!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
