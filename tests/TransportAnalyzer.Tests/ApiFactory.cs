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
        if (adminToken is not null) builder.UseSetting("Admin:Token", adminToken);
        builder.ConfigureTestServices(s =>
        {
            foreach (var a in adapters) s.AddSingleton(a);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(RawRoot))
            try { Directory.Delete(RawRoot, true); } catch (IOException) { }
    }
}
