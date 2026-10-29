using Kasper.Domain.Enums;

namespace Kasper.Domain.Entities;

public sealed class WorkflowStep
{
    private WorkflowStep() { }

    public Guid Id { get; private set; }
    public Guid WorkflowId { get; private set; }
    public int Sequence { get; private set; }
    /// <summary>Plan-local key such as "s1", used by later steps to reference outputs.</summary>
    public string Key { get; private set; } = "";
    public string ToolId { get; private set; } = "";
    public int ToolVersion { get; private set; }
    public string Purpose { get; private set; } = "";
    public EffectClass Effect { get; private set; }
    public StepStatus Status { get; private set; }
    /// <summary>Input as planned (may contain $s1.field references). Replaced when the user edits it.</summary>
    public string InputJson { get; private set; } = "{}";
    public string? OutputJson { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public int AttemptCount { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public string? ErrorCode { get; private set; }
    public string? ErrorSummary { get; private set; }

    public static WorkflowStep Create(Guid workflowId, int sequence, string key, string toolId, int toolVersion,
        string purpose, EffectClass effect, string inputJson) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = workflowId,
        Sequence = sequence,
        Key = key,
        ToolId = toolId,
        ToolVersion = toolVersion,
        Purpose = purpose,
        Effect = effect,
        Status = StepStatus.Pending,
        InputJson = inputJson,
        IdempotencyKey = $"{workflowId:N}:{sequence}",
    };

    public bool IsDone => Status is StepStatus.Succeeded or StepStatus.Failed or StepStatus.Cancelled;

    public void BeginAttempt(DateTime nowUtc)
    {
        Status = StepStatus.Running;
        StartedAtUtc ??= nowUtc;
        AttemptCount++;
    }

    public void Succeed(string outputJson, DateTime nowUtc)
    {
        Status = StepStatus.Succeeded;
        OutputJson = outputJson;
        CompletedAtUtc = nowUtc;
        ErrorCode = null;
        ErrorSummary = null;
    }

    public void Fail(string code, string summary, DateTime nowUtc)
    {
        Status = StepStatus.Failed;
        ErrorCode = code;
        ErrorSummary = summary;
        CompletedAtUtc = nowUtc;
    }

    public void RecordRetryableError(string code, string summary)
    {
        ErrorCode = code;
        ErrorSummary = summary;
    }

    public void WaitForApproval() => Status = StepStatus.WaitingForApproval;

    public void Cancel(DateTime nowUtc)
    {
        if (IsDone) return;
        Status = StepStatus.Cancelled;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>Back to Pending for an explicit retry or a safe replay after restart.</summary>
    public void ResetForRetry()
    {
        Status = StepStatus.Pending;
        CompletedAtUtc = null;
        ErrorCode = null;
        ErrorSummary = null;
    }

    public void ReplaceInput(string inputJson) => InputJson = inputJson;
}
