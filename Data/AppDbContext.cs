using Microsoft.EntityFrameworkCore;
using Management.Api.Models;

namespace Management.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ContainerMetric> ContainerMetrics { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // TimescaleDB requires the time column in every unique index of a hypertable; create_hypertable
        // adds its own index on RecordedAt.
        modelBuilder.Entity<ContainerMetric>()
            .HasKey(m => new { m.ContainerName, m.RecordedAt });
    }
}
