using Kasper.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kasper.Infrastructure.Persistence;

public sealed class KasperDbContext(DbContextOptions<KasperDbContext> options) : DbContext(options)
{
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Workflow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.CreatedBy).HasMaxLength(128);
            e.Property(x => x.RequestText).HasMaxLength(8000);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.FailureCode).HasMaxLength(64);
            e.Property(x => x.FailureSummary).HasMaxLength(2000);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(128);
            e.Property(x => x.EventType).HasMaxLength(64);
            e.Property(x => x.Target).HasMaxLength(512);
            e.Property(x => x.Outcome).HasMaxLength(64);
            e.HasIndex(x => x.WorkflowId);
            e.HasIndex(x => x.TimestampUtc);
        });
    }
}
