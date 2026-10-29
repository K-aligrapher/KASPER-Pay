using System.Text.Json;
using Kasper.Domain.Entities;
using Kasper.Infrastructure.Persistence;

namespace Kasper.Infrastructure.Runtime;

internal static class Audit
{
    /// <summary>Adds an audit event to the current unit of work. Metadata must not contain secrets or content.</summary>
    public static void Add(KasperDbContext db, DateTime nowUtc, string actor, string eventType, string outcome,
        Guid? workflowId = null, Guid? stepId = null, string? target = null, object? metadata = null) =>
        db.AuditEvents.Add(new AuditEvent
        {
            WorkflowId = workflowId,
            StepId = stepId,
            Actor = actor,
            EventType = eventType,
            Outcome = outcome,
            Target = target is { Length: > 500 } ? target[..500] : target,
            TimestampUtc = nowUtc,
            MetadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata),
        });
}
