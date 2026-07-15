using Microsoft.EntityFrameworkCore;
using Management.Api.Models;

namespace Management.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ContainerMetric> ContainerMetrics { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContainerMetric>()
            .HasIndex(m => m.RecordedAt);
    }
}
