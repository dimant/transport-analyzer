using Microsoft.EntityFrameworkCore;
using TransportAnalyzer.Infrastructure.Entities;

namespace TransportAnalyzer.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Exposure> Exposures => Set<Exposure>();
    public DbSet<IngestRun> IngestRuns => Set<IngestRun>();
    public DbSet<VocabularyEntry> Vocabulary => Set<VocabularyEntry>();
    public DbSet<VocabularyMapEntry> VocabularyMap => Set<VocabularyMapEntry>();
    public DbSet<Operator> Operators => Set<Operator>();
    public DbSet<EventOperator> EventOperators => Set<EventOperator>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Event>(e =>
        {
            e.ToTable("event");
            e.HasKey(x => x.EventId);
            e.HasIndex(x => new { x.SourceId, x.SourceKey }).IsUnique();
            e.Property(x => x.SourcePayload).HasColumnType("jsonb");
            e.Property(x => x.UnattributedFatalities).HasDefaultValue(0);
            e.Property(x => x.UnattributedSeriousInjuries).HasDefaultValue(0);
            e.Property(x => x.UnattributedMinorInjuries).HasDefaultValue(0);
            e.HasMany(x => x.Vehicles).WithOne().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Vehicle>(e =>
        {
            e.ToTable("vehicle");
            e.HasKey(x => x.VehicleId);
            e.HasIndex(x => new { x.EventId, x.VehicleNo }).IsUnique();
            e.Property(x => x.SourcePayload).HasColumnType("jsonb");
            e.Property(x => x.OccupantFatalities).HasDefaultValue(0);
            e.Property(x => x.OccupantSeriousInjuries).HasDefaultValue(0);
            e.Property(x => x.OccupantMinorInjuries).HasDefaultValue(0);
            e.Property(x => x.NonoccupantFatalities).HasDefaultValue(0);
            e.Property(x => x.NonoccupantSeriousInjuries).HasDefaultValue(0);
            e.Property(x => x.NonoccupantMinorInjuries).HasDefaultValue(0);
        });
        b.Entity<Exposure>(e =>
        {
            e.ToTable("exposure");
            e.HasKey(x => x.ExposureId);
            e.Property(x => x.Assumptions).HasColumnType("jsonb");
            // Unique index with NULLS NOT DISTINCT is created via raw SQL in the migration.
        });
        b.Entity<IngestRun>(e =>
        {
            e.ToTable("ingest_run");
            e.HasKey(x => x.IngestRunId);
            e.Property(x => x.Parameters).HasColumnType("jsonb");
            e.Property(x => x.RawFiles).HasColumnType("jsonb");
            e.Property(x => x.RowCounts).HasColumnType("jsonb");
            e.Property(x => x.Unmapped).HasColumnType("jsonb");
        });
        b.Entity<VocabularyEntry>(e =>
        {
            e.ToTable("vocabulary");
            e.HasKey(x => new { x.Attribute, x.Code });
        });
        b.Entity<VocabularyMapEntry>(e =>
        {
            e.ToTable("vocabulary_map");
            e.HasKey(x => new { x.SourceId, x.Attribute, x.SourceColumn, x.SourceValue });
        });
        b.Entity<Operator>(e =>
        {
            e.ToTable("operator");
            e.HasKey(x => x.OperatorId);
            e.Property(x => x.Identifiers).HasColumnType("jsonb");
        });
        b.Entity<EventOperator>(e =>
        {
            e.ToTable("event_operator");
            e.HasKey(x => new { x.EventId, x.OperatorId, x.Role });
        });
    }
}
