using Quartz;

namespace TransportAnalyzer.Api.Scheduling;

/// <summary>Reads <c>Sources:{sourceId}:Schedule</c> (Quartz cron, evaluated in UTC) for every registered source.
/// A missing or empty Schedule means the source is not scheduled.</summary>
public static class ScheduleRegistration
{
    public static IReadOnlyList<(string SourceId, string Cron)> Resolve(IConfiguration config, IEnumerable<string> registeredIds)
    {
        var ids = registeredIds.ToList();
        var result = new List<(string, string)>();
        foreach (var section in config.GetSection("Sources").GetChildren())
        {
            var cron = section["Schedule"];
            if (string.IsNullOrWhiteSpace(cron)) continue;
            cron = cron.Trim();
            if (!ids.Contains(section.Key))
                throw new InvalidOperationException(
                    $"Sources:{section.Key}:Schedule is set ('{cron}') but no source '{section.Key}' is registered.");
            try { _ = new CronExpression(cron); }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new InvalidOperationException(
                    $"Invalid cron expression '{cron}' for source '{section.Key}' (Sources:{section.Key}:Schedule): {ex.Message}", ex);
            }
            result.Add((section.Key, cron));
        }
        return result;
    }
}
