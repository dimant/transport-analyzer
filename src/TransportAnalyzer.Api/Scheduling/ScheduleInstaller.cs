using Quartz;
using TransportAnalyzer.Domain;

namespace TransportAnalyzer.Api.Scheduling;

/// <summary>Creates one cron trigger per configured source and logs the next fire times. An invalid or orphaned
/// schedule throws from StartAsync, which fails app startup.</summary>
public sealed class ScheduleInstaller(ISchedulerFactory factory, IEnumerable<ISourceAdapter> adapters,
    IConfiguration config, ILogger<ScheduleInstaller> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var schedules = ScheduleRegistration.Resolve(config, adapters.Select(a => a.Descriptor.SourceId));
        var scheduler = await factory.GetScheduler(ct);
        foreach (var (sourceId, cron) in schedules)
        {
            var job = JobBuilder.Create<IngestJob>().WithIdentity("ingest-" + sourceId)
                .UsingJobData(IngestJob.SourceIdKey, sourceId).Build();
            var trigger = TriggerBuilder.Create().WithIdentity("ingest-" + sourceId)
                .WithCronSchedule(cron, b => b.InTimeZone(TimeZoneInfo.Utc).WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing))
                .Build();
            var next = await scheduler.ScheduleJob(job, trigger, default, ct);
            logger.LogInformation("Scheduled ingest of {SourceId} with cron '{Cron}' (UTC); next fire {Next:u}", sourceId, cron, next);
        }
        if (schedules.Count == 0) logger.LogInformation("No ingest schedules configured");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
