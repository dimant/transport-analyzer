using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;
using TransportAnalyzer.Ingestion;

namespace TransportAnalyzer.Tests;

[Collection("db")]
public class IngestRunnerTests(PostgresFixture db)
{
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("hello,world\n1,2\n");

    private class FakeAdapter(string id = "fake") : ISourceAdapter
    {
        public SourceDescriptor Descriptor { get; } = new(id, "Fake", "batch", "events", "", "", "");
        public async Task<IReadOnlyList<RawFile>> FetchAsync(IRawStore raw, IngestParameters p, CancellationToken ct)
            => [await raw.SaveAsync(Descriptor.SourceId, "a.csv", new MemoryStream(Bytes), ct)];
        public Task StageAsync(IReadOnlyList<RawFile> files, NpgsqlConnection conn, IngestParameters p, CancellationToken ct) => Task.CompletedTask;
        public Task<NormalizeResult> NormalizeAsync(NpgsqlConnection conn, Guid runId, IngestParameters p, CancellationToken ct)
            => Task.FromResult(new NormalizeResult(new Dictionary<string, long> { ["event"] = 0 }, new Dictionary<string, long>()));
    }

    private sealed class ThrowingAdapter : ISourceAdapter
    {
        public SourceDescriptor Descriptor { get; } = new("throwing", "Throwing", "batch", "events", "", "", "");
        public Task<IReadOnlyList<RawFile>> FetchAsync(IRawStore raw, IngestParameters p, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RawFile>>([]);
        public async Task StageAsync(IReadOnlyList<RawFile> files, NpgsqlConnection conn, IngestParameters p, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(
                "create schema if not exists stg_test; create table stg_test.marker (id int); insert into stg_test.marker values (1)", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        public Task<NormalizeResult> NormalizeAsync(NpgsqlConnection conn, Guid runId, IngestParameters p, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Run_PersistsParametersWithCamelCaseKeys()
    {
        var runner = new IngestRunner(db.ConnectionString, new RawStore(Path.Combine(Path.GetTempPath(), "ta-raw-" + Guid.NewGuid().ToString("N"))));
        var id = await runner.RunAsync(new FakeAdapter("fake-params"), new IngestParameters([2019, 2020]), CancellationToken.None);
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select parameters::text from ingest_run where ingest_run_id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        Assert.Equal("{\"years\": [2019, 2020]}", (string)(await cmd.ExecuteScalarAsync())!);
    }

    private async Task<(string status, string rawFiles, string? error, bool finished)> GetRun(Guid id)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select status, raw_files::text, error, finished_at is not null from ingest_run where ingest_run_id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3));
    }

    [Fact]
    public async Task Run_Success_RecordsSucceededWithFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "ta-raw-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new IngestRunner(db.ConnectionString, new RawStore(root));
            var id = await runner.RunAsync(new FakeAdapter(), new IngestParameters(null), CancellationToken.None);
            var run = await GetRun(id);
            Assert.Equal("succeeded", run.status);
            Assert.True(run.finished);
            var files = JsonDocument.Parse(run.rawFiles).RootElement;
            Assert.Equal(1, files.GetArrayLength());
            // R24: persisted JSON object keys are camelCase.
            Assert.Equal(["name", "path", "sha256", "fetchedAt"], files[0].EnumerateObject().Select(p => p.Name));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Bytes)), files[0].GetProperty("sha256").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Run_SourceWithoutVehicles_AddsNoUnknownCounts()
    {
        var runner = new IngestRunner(db.ConnectionString, new RawStore(Path.Combine(Path.GetTempPath(), "ta-raw-" + Guid.NewGuid().ToString("N"))));
        var id = await runner.RunAsync(new FakeAdapter("fake-novehicles"), new IngestParameters(null), CancellationToken.None);
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select row_counts::text from ingest_run where ingest_run_id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        var rc = JsonSerializer.Deserialize<Dictionary<string, long>>((string)(await cmd.ExecuteScalarAsync())!)!;
        Assert.Equal(new Dictionary<string, long> { ["event"] = 0 }, rc);
    }

    [Fact]
    public async Task Run_Failure_RecordsFailedAndRollsBack()
    {
        var runner = new IngestRunner(db.ConnectionString, new RawStore(Path.GetTempPath()));
        var id = await runner.RunAsync(new ThrowingAdapter(), new IngestParameters(null), CancellationToken.None);
        var run = await GetRun(id);
        Assert.Equal("failed", run.status);
        Assert.Contains("boom", run.error);
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select to_regclass('stg_test.marker') is null", conn);
        Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task AdminIngest_UnknownSource_Returns404()
    {
        await using var factory = new ApiFactory(db);
        var res = await factory.CreateClient().PostAsync("/api/admin/ingest/nope", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task AdminIngest_WithTokenConfigured_RequiresHeader()
    {
        await using var factory = new ApiFactory(db, "t", new FakeAdapter("fake-token"));
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/admin/ingest/fake-token", null)).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/ingest/fake-token?years=2019,2020");
        req.Headers.Add("X-Admin-Token", "t");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var runId = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetGuid();

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((await GetRun(runId)).status == "running" && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.Equal("succeeded", (await GetRun(runId)).status);
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f)
        { if (level >= LogLevel.Error) Errors.Add(f(state, ex) + " | " + ex); }
    }

    private sealed class FlakyFinishRunner(string cs, ILogger log, string failOn) : IngestRunner(cs, new RawStore(Path.GetTempPath()), log)
    {
        public List<string> Attempts { get; } = [];
        protected override Task FinishAsync(Guid runId, string status, IReadOnlyList<RawFile> files,
            IReadOnlyDictionary<string, long> rowCounts, IReadOnlyDictionary<string, long> unmapped, string? error)
        {
            Attempts.Add(status);
            if (status == failOn) throw new NpgsqlException("db down");
            return base.FinishAsync(runId, status, files, rowCounts, unmapped, error);
        }
    }

    private async Task InsertRunning(Guid id, string sourceId)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "insert into ingest_run (ingest_run_id, source_id, started_at, status, parameters, raw_files, row_counts, unmapped) " +
            "values (@id, @s, now(), 'running', '{}', '[]', '{}', '{}')", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", sourceId);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Run_FailedStatusWriteFails_LogsBothAndDoesNotThrow()
    {
        var log = new ListLogger();
        var runner = new FlakyFinishRunner(db.ConnectionString, log, "failed");
        await runner.RunAsync(new ThrowingAdapter(), new IngestParameters(null), CancellationToken.None);
        var entry = Assert.Single(log.Errors);
        Assert.Contains("boom", entry);
        Assert.Contains("db down", entry);
    }

    [Fact]
    public async Task Run_SucceededStatusWriteFails_DoesNotRecordFailed()
    {
        var log = new ListLogger();
        var runner = new FlakyFinishRunner(db.ConnectionString, log, "succeeded");
        await runner.RunAsync(new FakeAdapter(), new IngestParameters(null), CancellationToken.None);
        Assert.Equal(["succeeded"], runner.Attempts);
        Assert.Single(log.Errors);
    }

    [Fact]
    public async Task Queue_Start_FailsLeftoverRunningRows()
    {
        var id = Guid.NewGuid();
        await InsertRunning(id, "sweep-src");
        var runner = new IngestRunner(db.ConnectionString, new RawStore(Path.GetTempPath()));
        var queue = new IngestQueue(runner, [], Microsoft.Extensions.Logging.Abstractions.NullLogger<IngestQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);
        await queue.StopAsync(CancellationToken.None);
        var run = await GetRun(id);
        Assert.Equal("failed", run.status);
        Assert.Contains("abandoned", run.error);
    }

    [Fact]
    public async Task AdminIngest_WhileRunning_Returns409WithExistingRunId()
    {
        await using var factory = new ApiFactory(db, null, new FakeAdapter("fake-409"));
        var client = factory.CreateClient(); // host (and its startup sweep) is up before the row is inserted
        var existing = Guid.NewGuid();
        await InsertRunning(existing, "fake-409");
        var res = await client.PostAsync("/api/admin/ingest/fake-409", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(existing, body.GetProperty("runId").GetGuid());
        Assert.True(body.TryGetProperty("error", out _));
    }
}
