using Kasper.Domain.Enums;

namespace Kasper.Domain.Entities;

/// <summary>
/// A Final Review request, bound to one exact action (FR-043). If the action hash changes,
/// the approval is invalidated and a new one is requested.
/// </summary>
public sealed class Approval
{
    private Approval() { }

    public Guid Id { get; private set; }
    public Guid WorkflowId { get; private set; }
    public Guid StepId { get; private set; }
    public string ActionHash { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string TargetDescription { get; private set; } = "";
    /// <summary>What the reviewer sees: fields, content preview, warnings (JSON).</summary>
    public string PreviewJson { get; private set; } = "{}";
    /// <summary>The fully resolved input the hash was computed from.</summary>
    public string ResolvedInputJson { get; private set; } = "{}";
    public ApprovalStatus Status { get; private set; }
    public DateTime RequestedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? DecidedBy { get; private set; }
    public string? Note { get; private set; }

    public static Approval Request(Guid workflowId, Guid stepId, string actionHash, string title, string target,
        string previewJson, string resolvedInputJson, DateTime nowUtc, TimeSpan lifetime) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = workflowId,
        StepId = stepId,
        ActionHash = actionHash,
        Title = title,
        TargetDescription = target,
        PreviewJson = previewJson,
        ResolvedInputJson = resolvedInputJson,
        Status = ApprovalStatus.Pending,
        RequestedAtUtc = nowUtc,
        ExpiresAtUtc = nowUtc + lifetime,
    };

    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;

    /// <summary>Only a human decision through the API calls this; the model never can (FR-045).</summary>
    public void Approve(string decidedBy, DateTime nowUtc) => Decide(ApprovalStatus.Approved, decidedBy, nowUtc, null);

    public void Reject(string decidedBy, DateTime nowUtc, string? note) => Decide(ApprovalStatus.Rejected, decidedBy, nowUtc, note);

    public void Invalidate(DateTime nowUtc, string reason)
    {
        if (Status is ApprovalStatus.Rejected or ApprovalStatus.Invalidated) return;
        Status = ApprovalStatus.Invalidated;
        DecidedAtUtc ??= nowUtc;
        Note = reason;
    }

    public void Expire(DateTime nowUtc)
    {
        if (Status != ApprovalStatus.Pending) return;
        Status = ApprovalStatus.Expired;
        DecidedAtUtc = nowUtc;
    }

    private void Decide(ApprovalStatus status, string decidedBy, DateTime nowUtc, string? note)
    {
        if (Status != ApprovalStatus.Pending)
            throw new InvalidOperationException($"Approval {Id} is already {Status}.");
        if (IsExpired(nowUtc))
            throw new InvalidOperationException($"Approval {Id} has expired.");
        Status = status;
        DecidedBy = decidedBy;
        DecidedAtUtc = nowUtc;
        Note = note;
    }
}
