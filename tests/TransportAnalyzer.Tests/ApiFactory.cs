using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TransportAnalyzer.Domain;

namespace TransportAnalyzer.Tests;

/// <summary>Test host over the shared Postgres container with a fresh temp raw-store root.
/// Adapters passed in are registered as ISourceAdapter; further changes can use WithWebHostBuilder.</summary>
public sealed class ApiFactory(PostgresFixture db, string? adminToken = null, params ISourceAdapter[] adapters)
    : WebApplicationFactory<Program>
{
    public string RawRoot { get; } = Path.Combine(Path.GetTempPath(), "ta-raw-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", db.ConnectionString);
        builder.UseSetting("RawStore:Root", RawRoot);
        builder.UseSetting("Scheduling:Enabled", "false");
        if (adminToken is not null) builder.UseSetting("Admin:Token", adminToken);
        builder.ConfigureTestServices(s =>
        {
            RemoveReplacedAdapters(s);
            foreach (var a in adapters) s.AddSingleton(a);
        });
    }

    /// <summary>Adapters passed in replace any registered adapter with the same SourceId (IngestQueue keys by id).
    /// Registered adapters are built once from a throwaway provider just to read their ids.</summary>
    private void RemoveReplacedAdapters(IServiceCollection s)
    {
        if (adapters.Length == 0) return;
        var descriptors = s.Where(d => d.ServiceType == typeof(ISourceAdapter)).ToList();
        using var probe = s.BuildServiceProvider();
        var built = probe.GetServices<ISourceAdapter>().ToList();
        var replaced = adapters.Select(a => a.Descriptor.SourceId).ToHashSet();
        for (var i = 0; i < descriptors.Count; i++)
            if (replaced.Contains(built[i].Descriptor.SourceId)) s.Remove(descriptors[i]);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(RawRoot))
            try { Directory.Delete(RawRoot, true); } catch (IOException) { }
    }
}
