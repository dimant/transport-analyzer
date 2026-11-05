using Microsoft.EntityFrameworkCore;
using Npgsql;
using TransportAnalyzer.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<AppDbContext>(o =>
        o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
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

app.Run();

public partial class Program;
