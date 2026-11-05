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
