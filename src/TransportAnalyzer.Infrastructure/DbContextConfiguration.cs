using Microsoft.EntityFrameworkCore;

namespace TransportAnalyzer.Infrastructure;

public static class DbContextConfiguration
{
    public static DbContextOptionsBuilder<AppDbContext> Configure(DbContextOptionsBuilder<AppDbContext> o, string connectionString)
    {
        o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
        return o;
    }
}
