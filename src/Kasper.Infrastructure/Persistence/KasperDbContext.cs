using Kasper.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kasper.Infrastructure.Persistence;

public sealed class KasperDbContext(DbContextOptions<KasperDbContext> options) : DbContext(options)
{
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowStep> Steps => Set<WorkflowStep>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<AllowedFolder> AllowedFolders => Set<AllowedFolder>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<MockOutboxEntry> MockOutbox => Set<MockOutboxEntry>();

    /// <summary>All times are stored as UTC; SQLite loses DateTimeKind, so restore it on read.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<DateTime>().HaveConversion<UtcConverter>();
        c.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    private sealed class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?>(
        v => v == null ? v : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime(),
        v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Workflow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.CreatedBy).HasMaxLength(128);
            e.Property(x => x.RequestText).HasMaxLength(8000);
            e.Property(x => x.Summary).HasMaxLength(300);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.FailureCode).HasMaxLength(64);
            e.Property(x => x.FailureSummary).HasMaxLength(2000);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        b.Entity<WorkflowStep>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Workflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.WorkflowId, x.Sequence }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(8);
            e.Property(x => x.ToolId).HasMaxLength(64);
            e.Property(x => x.Purpose).HasMaxLength(300);
            e.Property(x => x.Effect).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.IdempotencyKey).HasMaxLength(64);
            e.Property(x => x.ErrorCode).HasMaxLength(64);
            e.Property(x => x.ErrorSummary).HasMaxLength(2000);
        });

        b.Entity<Approval>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Workflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.WorkflowId, x.Status });
            e.HasIndex(x => x.StepId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ActionHash).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.TargetDescription).HasMaxLength(1000);
            e.Property(x => x.DecidedBy).HasMaxLength(128);
            e.Property(x => x.Note).HasMaxLength(1000);
        });

        b.Entity<Artifact>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Workflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.WorkflowId);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.StagingPath).HasMaxLength(1024);
            e.Property(x => x.CommittedPath).HasMaxLength(1024);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.MimeType).HasMaxLength(128);
        });

        b.Entity<AllowedFolder>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Path).HasMaxLength(1024);
            e.HasIndex(x => x.Path).IsUnique();
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

        b.Entity<MockOutboxEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.Property(x => x.IdempotencyKey).HasMaxLength(64);
            e.Property(x => x.Channel).HasMaxLength(128);
            e.Property(x => x.Text).HasMaxLength(4000);
        });
    }
}

/// <summary>Fake "external service" used by mock.external_post to test approvals and duplicate prevention.</summary>
public sealed class MockOutboxEntry
{
    public int Id { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string Channel { get; set; }
    public required string Text { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
