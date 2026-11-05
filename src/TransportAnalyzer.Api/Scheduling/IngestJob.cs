using Quartz;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Ingestion;

namespace TransportAnalyzer.Api.Scheduling;

public enum IngestJobOutcome { Enqueued, AlreadyRunning, Failed }

/// <summary>Scheduled ingest: enqueues a run for the source in the job data map. Never throws, so the scheduler keeps running.</summary>
[DisallowConcurrentExecution]
public sealed class IngestJob(IngestQueue queue, ILogger<IngestJob> logger) : IJob
{
    public const string SourceIdKey = "sourceId";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct) =>
        await RunAsync(context.MergedJobDataMap.GetString(SourceIdKey) ?? "");

    public async Task<IngestJobOutcome> RunAsync(string sourceId)
    {
        try
        {
            var (started, runId) = await queue.TryEnqueueAsync(sourceId, new IngestParameters(null));
            if (!started)
            {
                logger.LogInformation("Scheduled ingest of {SourceId}: already running (run {RunId}), skipped", sourceId, runId);
                return IngestJobOutcome.AlreadyRunning;
            }
            logger.LogInformation("Scheduled ingest of {SourceId} enqueued as run {RunId}", sourceId, runId);
            return IngestJobOutcome.Enqueued;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled ingest of {SourceId} failed to start", sourceId);
            return IngestJobOutcome.Failed;
        }
    }
}
