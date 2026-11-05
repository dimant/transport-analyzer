using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using TransportAnalyzer.Api.Endpoints;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;
using TransportAnalyzer.Ingestion;
using TransportAnalyzer.Ingestion.Bts;
using TransportAnalyzer.Ingestion.Fars;
using TransportAnalyzer.Ingestion.Ntsb;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<AppDbContext>(o =>
        o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

    var rawRoot = builder.Configuration["RawStore:Root"] ?? Path.Combine(AppContext.BaseDirectory, "raw");
    builder.Services.AddSingleton<IRawStore>(new RawStore(rawRoot));
    builder.Services.AddSingleton(sp => new IngestRunner(connectionString, sp.GetRequiredService<IRawStore>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<IngestRunner>()));
    builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection("Ingest"));
    builder.Services.AddSingleton<IngestQueue>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestQueue>());

    builder.Services.Configure<FarsOptions>(builder.Configuration.GetSection("Sources:fars"));
    builder.Services.AddHttpClient(nameof(FarsAdapter), c => c.Timeout = TimeSpan.FromMinutes(10));
    builder.Services.AddSingleton<ISourceAdapter>(sp => new FarsAdapter(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(FarsAdapter)),
        sp.GetRequiredService<IOptions<FarsOptions>>()));

    builder.Services.AddSingleton<IMdbExporter>(new MdbToolsExporter());
    builder.Services.Configure<NtsbOptions>(builder.Configuration.GetSection("Sources:ntsb"));
    builder.Services.AddHttpClient(nameof(NtsbAdapter), c => c.Timeout = TimeSpan.FromMinutes(30));
    builder.Services.AddSingleton<ISourceAdapter>(sp => new NtsbAdapter(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(NtsbAdapter)),
        sp.GetRequiredService<IMdbExporter>(),
        sp.GetRequiredService<IOptions<NtsbOptions>>()));

    // BTS tables are downloaded by hand into {Inbox:Root}/{sourceId}/. Unset: the nearest data/inbox walking up
    // from the working directory (works for `dotnet run` in any project folder), else ./data/inbox.
    var inboxRoot = builder.Configuration["Inbox:Root"] ?? FindInboxRoot();
    var btsDataRoot = builder.Configuration["Data:Root"] ?? Path.Combine(AppContext.BaseDirectory, "data");
    foreach (var defaults in new[] { BtsTableOptions.PassengerMiles(), BtsTableOptions.VehicleMiles() })
    {
        var options = defaults;
        builder.Configuration.GetSection($"Sources:{options.SourceId}").Bind(options);
        options.InboxDir = Path.Combine(inboxRoot, options.SourceId);
        builder.Services.AddSingleton<ISourceAdapter>(new BtsNtsTableAdapter(options, btsDataRoot));
    }
}

var app = builder.Build();

// Migrate and load vocabulary at startup; skipped when no connection string is configured.
if (!string.IsNullOrWhiteSpace(connectionString))
{
    var dataRoot = app.Configuration["Data:Root"] ?? Path.Combine(AppContext.BaseDirectory, "data");
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await VocabularyLoader.LoadAsync(conn, dataRoot, CancellationToken.None);
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

if (!string.IsNullOrWhiteSpace(connectionString))
{
    app.MapAdminEndpoints(connectionString);
    app.MapSourceEndpoints(connectionString);
    app.MapEventEndpoints(connectionString);
    app.MapExposureEndpoints(connectionString);
}

app.Run();

static string FindInboxRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "data", "inbox");
        if (Directory.Exists(candidate)) return candidate;
    }
    return Path.Combine(Directory.GetCurrentDirectory(), "data", "inbox");
}

public partial class Program;
