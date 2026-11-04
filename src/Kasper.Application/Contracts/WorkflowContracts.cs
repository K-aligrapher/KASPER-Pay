using System.Text.Json.Nodes;
using Kasper.Application.Tools;

namespace Kasper.Application.Contracts;

public sealed record AssistantRequestDto(IReadOnlyList<ChatMessageDto> Messages);

/// <summary>Kind is "reply" (just an answer) or "workflow" (a task was started).</summary>
public sealed record AssistantResponseDto(string Kind, string? Reply, WorkflowSummaryDto? Workflow);

public sealed record PlanStepDto(string Id, string Tool, string? Purpose, JsonObject Input);

public sealed record PlanDto(string Summary, IReadOnlyList<PlanStepDto> Steps);

/// <summary>Create from text (KASPER plans it) or from an explicit plan (quick actions, scripts).</summary>
public sealed record CreateWorkflowDto(string Request, PlanDto? Plan = null);

public sealed record WorkflowSummaryDto(
    Guid Id,
    string Summary,
    string RequestText,
    string Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? CompletedAtUtc,
    string? FailureCode,
    string? FailureSummary,
    int PendingApprovals,
    int StepCount);

public sealed record StepDto(
    Guid Id,
    int Sequence,
    string Key,
    string ToolId,
    string Purpose,
    string Effect,
    bool RequiresApproval,
    string Status,
    JsonNode? Input,
    JsonNode? Output,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    int AttemptCount,
    string? ErrorCode,
    string? ErrorSummary);

public sealed record EditableFieldDto(string Name, string Description, string Type, string? Value, IReadOnlyList<OptionDto>? Options);

public sealed record OptionDto(string Value, string Label);

public sealed record ApprovalDto(
    Guid Id,
    Guid WorkflowId,
    Guid StepId,
    string WorkflowSummary,
    string Title,
    string Target,
    string Status,
    DateTime RequestedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? DecidedAtUtc,
    string? DecidedBy,
    string? Note,
    IReadOnlyList<PreviewField> Fields,
    string? Content,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<EditableFieldDto> Editable,
    Guid? PreviewArtifactId = null);

/// <summary>Decision is "approve", "reject" or "edit". Edits are only allowed for editable fields.</summary>
public sealed record DecisionDto(string Decision, IReadOnlyDictionary<string, string>? Edits = null, string? Note = null);

public sealed record ArtifactDto(Guid Id, string FileName, long SizeBytes, string Sha256, string? CommittedPath, bool Staged);

public sealed record AuditDto(DateTime TimestampUtc, string Actor, string EventType, string? Target, string Outcome);

public sealed record WorkflowDetailDto(
    WorkflowSummaryDto Workflow,
    IReadOnlyList<StepDto> Steps,
    IReadOnlyList<ApprovalDto> Approvals,
    IReadOnlyList<ArtifactDto> Artifacts,
    IReadOnlyList<AuditDto> Audit);

public sealed record FolderDto(int Id, string Ref, string Path, bool IsDefault, bool Exists);

public sealed record AddFolderDto(string Path);

public sealed record WorkflowEventDto(Guid WorkflowId, string Status, string Summary, int PendingApprovals);
