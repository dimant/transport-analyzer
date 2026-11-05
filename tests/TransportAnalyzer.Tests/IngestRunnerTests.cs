using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Bytes)), files[0].GetProperty("Sha256").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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
}
