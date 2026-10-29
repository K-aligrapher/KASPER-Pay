using Kasper.Domain.Enums;
using Kasper.Domain.Rules;

namespace Kasper.Domain.Entities;

public sealed class Workflow
{
    private Workflow() { }

    public Guid Id { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public string RequestText { get; private set; } = "";
    public WorkflowStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public string? FailureCode { get; private set; }
    public string? FailureSummary { get; private set; }

    public static Workflow Create(string createdBy, string requestText, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        CreatedBy = createdBy,
        RequestText = requestText,
        Status = WorkflowStatus.Pending,
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc,
        CorrelationId = Guid.NewGuid().ToString("N"),
    };

    public void TransitionTo(WorkflowStatus next, DateTime nowUtc, string? failureCode = null, string? failureSummary = null)
    {
        if (!WorkflowStateMachine.CanTransition(Status, next))
            throw new InvalidOperationException($"Workflow {Id}: {Status} -> {next} is not allowed.");

        Status = next;
        UpdatedAtUtc = nowUtc;
        if (next == WorkflowStatus.Running) StartedAtUtc ??= nowUtc;
        if (next is WorkflowStatus.Succeeded or WorkflowStatus.Failed or WorkflowStatus.Cancelled) CompletedAtUtc = nowUtc;
        if (next == WorkflowStatus.Failed)
        {
            FailureCode = failureCode;
            FailureSummary = failureSummary;
        }
        if (next == WorkflowStatus.Pending)
        {
            FailureCode = null;
            FailureSummary = null;
            CompletedAtUtc = null;
        }
    }
}
