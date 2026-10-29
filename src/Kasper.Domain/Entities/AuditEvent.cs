namespace Kasper.Domain.Entities;

public sealed class AuditEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? WorkflowId { get; init; }
    public Guid? StepId { get; init; }
    public required string Actor { get; init; }
    public required string EventType { get; init; }
    public DateTime TimestampUtc { get; init; }
    public string? Target { get; init; }
    public required string Outcome { get; init; }
    /// <summary>Must already be redacted. Never put secrets or document text here.</summary>
    public string? MetadataJson { get; init; }
}
