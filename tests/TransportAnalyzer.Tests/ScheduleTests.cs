using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quartz;
using Microsoft.Extensions.Logging.Abstractions;
using TransportAnalyzer.Api.Scheduling;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;
using TransportAnalyzer.Ingestion;

namespace TransportAnalyzer.Tests;

[Collection("db")]
public class ScheduleTests(PostgresFixture db)
{
    private static readonly string[] AllIds = ["fars", "ntsb", "faa-ga-survey", "bts-passenger-miles", "bts-vehicle-miles"];

    private static IConfiguration Config(params (string key, string value)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.key, x.value))).Build();

    [Fact]
    public void Registration_CreatesTriggerPerConfiguredSource()
    {
        var cfg = Config(("Sources:fars:Schedule", "0 0 3 1 * ?"), ("Sources:ntsb:Schedule", "0 0 4 2 * ?"),
            ("Sources:faa-ga-survey:Schedule", "0 0 5 1 3 ?"), ("Sources:bts-vehicle-miles:Schedule", ""),
            ("Sources:bts-passenger-miles:Url", "x"));
        var result = ScheduleRegistration.Resolve(cfg, AllIds);
        Assert.Equal([("fars", "0 0 3 1 * ?"), ("ntsb", "0 0 4 2 * ?"), ("faa-ga-survey", "0 0 5 1 3 ?")],
            result.Select(r => (r.SourceId, r.Cron)).OrderBy(r => Array.IndexOf(AllIds, r.SourceId)).ToArray());
    }

    [Fact]
    public void Registration_InvalidCron_ThrowsNamingSource()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScheduleRegistration.Resolve(Config(("Sources:ntsb:Schedule", "not a cron")), AllIds));
        Assert.Contains("ntsb", ex.Message);
        Assert.Contains("not a cron", ex.Message);
    }

    [Fact]
    public void Registration_ScheduleForUnregisteredSource_ThrowsNamingSource()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScheduleRegistration.Resolve(Config(("Sources:ghost:Schedule", "0 0 3 1 * ?")), AllIds));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public async Task IngestJob_SourceAlreadyRunning_SkipsWithoutThrowing()
    {
        var sourceId = "t8-" + Guid.NewGuid().ToString("N");
        var runner = new IngestRunner(db.ConnectionString, new RawStore(Path.GetTempPath()));
        var queue = new IngestQueue(runner, [], NullLogger<IngestQueue>.Instance);
        var running = await runner.CreateRunAsync(sourceId, new IngestParameters(null), CancellationToken.None);

        var outcome = await new IngestJob(queue, NullLogger<IngestJob>.Instance).RunAsync(sourceId);

        Assert.Equal(IngestJobOutcome.AlreadyRunning, outcome);
        Assert.Equal(running, await runner.FindRunningAsync(sourceId, CancellationToken.None));
    }

    [Fact]
    public async Task IngestJob_EnqueueFails_IsSwallowed()
    {
        var runner = new IngestRunner("Host=127.0.0.1;Port=1;Database=x;Timeout=1", new RawStore(Path.GetTempPath()));
        var queue = new IngestQueue(runner, [], NullLogger<IngestQueue>.Instance);
        var outcome = await new IngestJob(queue, NullLogger<IngestJob>.Instance).RunAsync("fars");
        Assert.Equal(IngestJobOutcome.Failed, outcome);
    }

    private sealed class NoopAdapter(string id) : ISourceAdapter
    {
        public SourceDescriptor Descriptor { get; } = new(id, "Noop", "batch", "events", "", "", "");
        public Task<IReadOnlyList<RawFile>> FetchAsync(IRawStore raw, IngestParameters p, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RawFile>>([]);
        public Task StageAsync(IReadOnlyList<RawFile> files, NpgsqlConnection conn, IngestParameters p, CancellationToken ct) => Task.CompletedTask;
        public Task<NormalizeResult> NormalizeAsync(NpgsqlConnection conn, Guid runId, IngestParameters p, CancellationToken ct)
            => Task.FromResult(new NormalizeResult(new Dictionary<string, long>(), new Dictionary<string, long>()));
    }

    private async Task<string?> RunStatusAsync(string sourceId)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select status from ingest_run where source_id = @s order by started_at desc limit 1", conn);
        cmd.Parameters.AddWithValue("s", sourceId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    [Fact]
    public async Task Host_WithSchedule_TriggerFiresIngestRun_UtcAndMisfireDoNothing()
    {
        var sourceId = "sched-" + Guid.NewGuid().ToString("N");
        await using var baseFactory = new ApiFactory(db, null, new NoopAdapter(sourceId));
        await using var factory = baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Scheduling:Enabled", "true");
            b.UseSetting($"Sources:{sourceId}:Schedule", "0 0 3 1 1 ? 2099"); // far future: only the manual trigger fires
        });
        var scheduler = await factory.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();

        var trigger = Assert.IsAssignableFrom<ICronTrigger>(await scheduler.GetTrigger(new TriggerKey("ingest-" + sourceId)));
        Assert.Equal(TimeZoneInfo.Utc.Id, trigger.TimeZone.Id);
        Assert.Equal((int)CronTriggerMisfireInstruction.DoNothing, trigger.MisfireInstructionCode);
        Assert.Null(await RunStatusAsync(sourceId));

        await scheduler.TriggerJob(new JobKey("ingest-" + sourceId));

        var deadline = DateTime.UtcNow.AddSeconds(20);
        string? status;
        while ((status = await RunStatusAsync(sourceId)) is null or "running" && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal("succeeded", status);
    }
}
