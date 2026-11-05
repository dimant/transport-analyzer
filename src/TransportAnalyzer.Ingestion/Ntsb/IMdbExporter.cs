using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace TransportAnalyzer.Ingestion.Ntsb;

/// <summary>Reads tables out of an Access MDB file. The seam exists so adapter tests can use a fake.</summary>
public interface IMdbExporter
{
    Task<IReadOnlyList<string>> ListTablesAsync(string mdbPath, CancellationToken ct);

    /// <summary>CSV in the mdb-export default dialect (header row, quoted text, embedded newlines inside quotes).</summary>
    Task<Stream> ExportAsync(string mdbPath, string table, CancellationToken ct);
}

/// <summary>Runs the mdbtools binaries without a shell.</summary>
public sealed class MdbToolsExporter(string mdbTablesPath = "mdb-tables", string mdbExportPath = "mdb-export") : IMdbExporter
{
    public async Task<IReadOnlyList<string>> ListTablesAsync(string mdbPath, CancellationToken ct)
    {
        using var process = Start(mdbTablesPath, ["-1", mdbPath]);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            var output = await stdout;
            var err = await stderr;
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw Failure(mdbTablesPath, process.ExitCode, err);
            return output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }
        catch { Kill(process); throw; }
    }

    public Task<Stream> ExportAsync(string mdbPath, string table, CancellationToken ct)
    {
        var process = Start(mdbExportPath, [mdbPath, table]);
        return Task.FromResult<Stream>(new ProcessOutputStream(process, mdbExportPath));
    }

    private static Process Start(string binary, string[] args)
    {
        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            return Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{binary}'.");
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException(
                $"Could not run '{binary}': {e.Message}. Is mdbtools installed and on PATH?", e);
        }
    }

    private static InvalidOperationException Failure(string binary, int exitCode, string stderr) =>
        new($"{binary} failed with exit code {exitCode}: {stderr.Trim()}");

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { }
    }

    /// <summary>Streams stdout; at end of stream waits for exit and surfaces a non-zero exit code as an exception.</summary>
    private sealed class ProcessOutputStream : Stream
    {
        private readonly Process _process;
        private readonly string _binary;
        private readonly Stream _stdout;
        private readonly Task<string> _stderr;
        private bool _finished;

        public ProcessOutputStream(Process process, string binary)
        {
            _process = process;
            _binary = binary;
            _stdout = process.StandardOutput.BaseStream;
            _stderr = process.StandardError.ReadToEndAsync(); // drained concurrently so stderr cannot block the child
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await _stdout.ReadAsync(buffer, ct);
            if (n == 0 && !_finished) await FinishAsync(ct);
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private async Task FinishAsync(CancellationToken ct)
        {
            _finished = true;
            await _process.WaitForExitAsync(ct);
            var err = await _stderr;
            if (_process.ExitCode != 0) throw Failure(_binary, _process.ExitCode, err);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Kill(_process);
                _process.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
