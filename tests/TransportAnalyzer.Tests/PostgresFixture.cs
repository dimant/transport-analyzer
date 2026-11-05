using Npgsql;
using Testcontainers.PostgreSql;
using TransportAnalyzer.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace TransportAnalyzer.Tests;

public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var options = DbContextConfiguration.Configure(new DbContextOptionsBuilder<AppDbContext>(), ConnectionString).Options;
        await using (var db = new AppDbContext(options))
            await db.Database.MigrateAsync();
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await VocabularyLoader.LoadAsync(conn, Path.Combine(AppContext.BaseDirectory, "data"), CancellationToken.None);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("db")]
public class DbCollection : ICollectionFixture<PostgresFixture>;
