using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TransportAnalyzer.Infrastructure;

/// <summary>Used only by `dotnet ef` tooling; never connects.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(DbContextConfiguration.Configure(new DbContextOptionsBuilder<AppDbContext>(), "Host=localhost;Database=design").Options);
}
