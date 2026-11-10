using System.Text.Json.Nodes;
using Kasper.Domain.Enums;

namespace Kasper.Application.Tools;

public enum ParamType
{
    /// <summary>Free text (may be a $ref to a previous step's text output).</summary>
    Text,
    /// <summary>A bare file name like "notes.md": no folders, no reserved names.</summary>
    FileName,
    /// <summary>An allowed folder reference like "folder:1", issued by KASPER in the plan context.</summary>
    FolderRef,
    /// <summary>An artifact produced by an earlier step, always a $ref like "$s1.artifact".</summary>
    ArtifactRef,
    Integer,
    /// <summary>A JSON array of artifact references, e.g. ["$s1.artifact", "$s2.artifact"].</summary>
    ArtifactList,
    /// <summary>An allowlisted Slack channel, e.g. "#team".</summary>
    Channel,
    /// <summary>An allowlisted GitHub repository, e.g. "owner/name".</summary>
    Repo,
    /// <summary>A one-time in-memory secret reference ("secret:…"); the value is never stored.</summary>
    Secret,
}

public sealed record ToolParameter(
    string Name,
    ParamType Type,
    bool Required,
    string Description,
    int MaxLength = 4000,
    bool Editable = false);

/// <summary>Tool contract (PRD §15). Approval comes from <see cref="Effect"/>, never from the model.</summary>
public sealed record ToolDescriptor(
    string Id,
    int Version,
    string Description,
    EffectClass Effect,
    bool WritesOnlyToStaging,
    IReadOnlyList<ToolParameter> Parameters,
    IReadOnlyList<string> Outputs,
    TimeSpan Timeout,
    int MaxAttempts,
    bool Idempotent,
    bool DevOnly = false,
    bool HiddenFromPlanner = false,
    string? Integration = null)
{
    public bool RequiresApproval => Domain.Rules.ApprovalPolicy.RequiresApproval(Effect, WritesOnlyToStaging);
}

public sealed record ToolContext(Guid WorkflowId, Guid StepId, string IdempotencyKey, int Attempt);

public sealed record ToolResult(JsonObject Output);

public sealed record PreviewField(string Label, string Value);

/// <summary>What Final Review shows for a pending action (FR-046).</summary>
public sealed record ToolPreview(
    string Title,
    string Target,
    IReadOnlyList<PreviewField> Fields,
    string? Content = null,
    IReadOnlyList<string>? Warnings = null);

public interface ITool
{
    ToolDescriptor Descriptor { get; }

    Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct);

    /// <summary>Describe exactly what will happen, without doing it. Only called for approval-gated tools.</summary>
    Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct) =>
        Task.FromResult(new ToolPreview(Descriptor.Description, Descriptor.Id, []));

    /// <summary>
    /// Extra facts bound into the approval fingerprint (e.g. the code-derived price), so the approval
    /// is invalid if they change. Null for tools whose input already says everything.
    /// </summary>
    Task<JsonObject?> FingerprintExtrasAsync(JsonObject input, CancellationToken ct) => Task.FromResult<JsonObject?>(null);

    /// <summary>
    /// A standing mandate the owner set up in advance may pre-approve a small action. Returns why it was
    /// pre-approved, or null to require Final Review as usual. Never called for the model's benefit:
    /// the limits live in settings and are checked in code.
    /// </summary>
    Task<string?> TryAutoApproveAsync(JsonObject input, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>
    /// Checked when a plan is submitted, before anything is staged. Returns reasons the action must be
    /// blocked (empty = fine). Only called with literal inputs, never with unresolved $references.
    /// </summary>
    Task<IReadOnlyList<ScreenViolation>> ScreenAsync(JsonObject input, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ScreenViolation>>([]);
}

/// <summary>A rule an action broke, e.g. Rule = "per_transaction_limit".</summary>
public sealed record ScreenViolation(string Rule, string Message);

public sealed class ToolException(string code, string message, ErrorCategory category, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public ErrorCategory Category { get; } = category;
    public bool Retryable => Category is ErrorCategory.Transient or ErrorCategory.RateLimit;
}

/// <summary>Tells the registry which integrations are connected (tools of others are hidden).</summary>
public interface IIntegrationStatus
{
    bool IsConnected(string integration);
}

public interface IToolRegistry
{
    /// <summary>Tools the model may use right now (mock tools only when enabled).</summary>
    IReadOnlyList<ToolDescriptor> Available { get; }

    ITool? Find(string toolId);
}
