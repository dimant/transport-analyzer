using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using TransportAnalyzer.Api.Endpoints;
using TransportAnalyzer.Domain;
using TransportAnalyzer.Infrastructure;
using TransportAnalyzer.Ingestion;
using TransportAnalyzer.Ingestion.Fars;

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
    builder.Services.AddSingleton<IngestQueue>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestQueue>());

    builder.Services.Configure<FarsOptions>(builder.Configuration.GetSection("Sources:fars"));
    builder.Services.AddHttpClient(nameof(FarsAdapter), c => c.Timeout = TimeSpan.FromMinutes(10));
    builder.Services.AddSingleton<ISourceAdapter>(sp => new FarsAdapter(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(FarsAdapter)),
        sp.GetRequiredService<IOptions<FarsOptions>>()));
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
    app.MapAdminEndpoints(connectionString);

app.Run();

public partial class Program;
