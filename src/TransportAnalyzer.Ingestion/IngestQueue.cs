using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TransportAnalyzer.Domain;

namespace TransportAnalyzer.Ingestion;

public sealed class IngestQueue(IngestRunner runner, IEnumerable<ISourceAdapter> adapters, ILogger<IngestQueue> logger)
    : BackgroundService
{
    private readonly Channel<(string sourceId, IngestParameters p, Guid runId)> _channel =
        Channel.CreateUnbounded<(string, IngestParameters, Guid)>();
    private readonly Dictionary<string, ISourceAdapter> _adapters =
        adapters.ToDictionary(a => a.Descriptor.SourceId);

    public bool IsKnownSource(string sourceId) => _adapters.ContainsKey(sourceId);

    /// <summary>Pre-inserts the run as 'running' so the id is returned immediately, then queues the work.</summary>
    public async Task<Guid> EnqueueAsync(string sourceId, IngestParameters p)
    {
        var runId = await runner.CreateRunAsync(sourceId, p, CancellationToken.None);
        await _channel.Writer.WriteAsync((sourceId, p, runId));
        return runId;
    }

    private readonly SemaphoreSlim _enqueueLock = new(1, 1);

    /// <summary>Like EnqueueAsync, but refuses when a run for the source is already 'running'.</summary>
    public async Task<(bool Started, Guid RunId)> TryEnqueueAsync(string sourceId, IngestParameters p)
    {
        await _enqueueLock.WaitAsync();
        try
        {
            if (await runner.FindRunningAsync(sourceId, CancellationToken.None) is { } existing)
                return (false, existing);
            return (true, await EnqueueAsync(sourceId, p));
        }
        finally { _enqueueLock.Release(); }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Runs left 'running' by a previous process can never finish; fail them so they do not block new runs.
        try { await runner.AbandonRunningAsync(cancellationToken); }
        catch (Exception ex) { logger.LogError(ex, "Could not sweep abandoned ingest runs"); }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (sourceId, p, runId) in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try { await runner.ExecuteAsync(_adapters[sourceId], p, runId, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Ingest run {RunId} for {SourceId} crashed", runId, sourceId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
