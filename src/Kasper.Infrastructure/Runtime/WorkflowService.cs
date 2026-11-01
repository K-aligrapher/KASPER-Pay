using System.Text.Json;
using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Application.Options;
using Kasper.Application.Planning;
using Kasper.Application.Tools;
using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Files;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Runtime;

/// <summary>Errors the API turns into 400/404/409 responses.</summary>
public sealed class WorkflowRequestException(int status, string code, string message, IReadOnlyList<string>? details = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IReadOnlyList<string> Details { get; } = details ?? [];
}

public sealed class WorkflowService(
    KasperDbContext db,
    IToolRegistry tools,
    FileScope scope,
    IClock clock,
    IWorkflowEvents events,
    RunningWorkflows running,
    IOptionsMonitor<RuntimeOptions> options)
{
    /// <summary>The person using this PC. KASPER v1 has a single local operator.</summary>
    public static string Operator => Environment.UserName;

    public async Task<WorkflowSummaryDto> CreateAsync(string request, Plan plan, CancellationToken ct, int? maxSteps = null)
    {
        if (string.IsNullOrWhiteSpace(request) || request.Length > 8000)
            throw new WorkflowRequestException(400, "invalid_request", "The request must be 1–8000 characters.");

        await scope.EnsureDefaultAsync(clock.UtcNow, ct);
        var folderRefs = (await scope.ListAsync(ct)).Select(FileScope.RefFor).ToHashSet();
        var errors = PlanValidator.Validate(plan, tools, folderRefs, maxSteps ?? options.CurrentValue.MaxSteps);
        if (errors.Count > 0)
            throw new WorkflowRequestException(400, "plan_invalid", "The plan was rejected by validation.", errors);

        var wf = Workflow.Create(Operator, request, plan.Summary, clock.UtcNow);
        db.Workflows.Add(wf);
        var seq = 1;
        foreach (var s in plan.Steps)
        {
            var d = tools.Find(s.ToolId)!.Descriptor;
            db.Steps.Add(WorkflowStep.Create(wf.Id, seq++, s.Key, d.Id, d.Version,
                string.IsNullOrWhiteSpace(s.Purpose) ? d.Description : s.Purpose, d.Effect, s.Input.ToJsonString()));
        }
        Audit.Add(db, clock.UtcNow, Operator, "workflow.created", "pending", wf.Id, metadata: new { steps = plan.Steps.Count });
        await db.SaveChangesAsync(ct);

        events.Publish(new WorkflowEventDto(wf.Id, wf.Status.ToString(), wf.Summary, 0));
        events.SignalWorker();
        return Summary(wf, 0, plan.Steps.Count);
    }

    public async Task<IReadOnlyList<WorkflowSummaryDto>> ListAsync(int take, CancellationToken ct)
    {
        var list = await db.Workflows.OrderByDescending(w => w.CreatedAtUtc).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        var ids = list.Select(w => w.Id).ToList();
        var pending = await db.Approvals.Where(a => ids.Contains(a.WorkflowId) && a.Status == ApprovalStatus.Pending)
            .GroupBy(a => a.WorkflowId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var stepCounts = await db.Steps.Where(s => ids.Contains(s.WorkflowId))
            .GroupBy(s => s.WorkflowId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return list.Select(w => Summary(w, pending.GetValueOrDefault(w.Id), stepCounts.GetValueOrDefault(w.Id))).ToList();
    }

    public async Task<WorkflowDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var wf = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new WorkflowRequestException(404, "not_found", "No such workflow.");
        var steps = await db.Steps.AsNoTracking().Where(s => s.WorkflowId == id).OrderBy(s => s.Sequence).ToListAsync(ct);
        var approvals = await db.Approvals.AsNoTracking().Where(a => a.WorkflowId == id).OrderBy(a => a.RequestedAtUtc).ToListAsync(ct);
        var artifacts = await db.Artifacts.AsNoTracking().Where(a => a.WorkflowId == id).OrderBy(a => a.CreatedAtUtc).ToListAsync(ct);
        var audit = await db.AuditEvents.AsNoTracking().Where(a => a.WorkflowId == id).OrderBy(a => a.TimestampUtc).ToListAsync(ct);
        var folders = await scope.ListAsync(ct);

        return new WorkflowDetailDto(
            Summary(wf, approvals.Count(a => a.Status == ApprovalStatus.Pending), steps.Count),
            steps.Select(ToDto).ToList(),
            approvals.Select(a => ToDto(a, wf, steps, folders)).ToList(),
            artifacts.Select(a => new ArtifactDto(a.Id, a.FileName, a.SizeBytes, a.Sha256, a.CommittedPath, File.Exists(a.StagingPath))).ToList(),
            audit.Select(a => new AuditDto(a.TimestampUtc, a.Actor, a.EventType, a.Target, a.Outcome)).ToList());
    }

    public async Task<IReadOnlyList<ApprovalDto>> PendingApprovalsAsync(CancellationToken ct)
    {
        var approvals = await db.Approvals.AsNoTracking().Where(a => a.Status == ApprovalStatus.Pending)
            .OrderBy(a => a.RequestedAtUtc).ToListAsync(ct);
        var wfIds = approvals.Select(a => a.WorkflowId).Distinct().ToList();
        var workflows = await db.Workflows.AsNoTracking().Where(w => wfIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var steps = await db.Steps.AsNoTracking().Where(s => wfIds.Contains(s.WorkflowId)).ToListAsync(ct);
        var folders = await scope.ListAsync(ct);
        return approvals
            .Where(a => workflows.ContainsKey(a.WorkflowId))
            .Select(a => ToDto(a, workflows[a.WorkflowId], steps.Where(s => s.WorkflowId == a.WorkflowId).ToList(), folders))
            .ToList();
    }

    /// <summary>A human decision (FR-044). Approval is recorded here and re-checked by the engine before acting.</summary>
    public async Task<ApprovalDto?> DecideAsync(Guid approvalId, DecisionDto decision, CancellationToken ct)
    {
        var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == approvalId, ct)
            ?? throw new WorkflowRequestException(404, "not_found", "No such approval.");
        var wf = await db.Workflows.FirstAsync(w => w.Id == approval.WorkflowId, ct);
        var now = clock.UtcNow;

        if (approval.Status != ApprovalStatus.Pending)
            throw new WorkflowRequestException(409, "already_decided", $"This review is already {approval.Status.ToString().ToLowerInvariant()}.");
        if (approval.IsExpired(now))
        {
            approval.Expire(now);
            await db.SaveChangesAsync(ct);
            events.SignalWorker();
            throw new WorkflowRequestException(409, "expired", "This review expired. KASPER will ask again with a fresh preview.");
        }

        switch (decision.Decision?.ToLowerInvariant())
        {
            case "approve":
                approval.Approve(Operator, now);
                Audit.Add(db, now, Operator, "approval.approved", "approved", wf.Id, approval.StepId, approval.TargetDescription);
                break;

            case "reject":
                approval.Reject(Operator, now, Trim(decision.Note, 1000));
                Audit.Add(db, now, Operator, "approval.rejected", "rejected", wf.Id, approval.StepId, approval.TargetDescription);
                break;

            case "edit":
                await ApplyEditsAsync(approval, decision.Edits ?? new Dictionary<string, string>(), ct);
                approval.Invalidate(now, "Edited by you; a new review was created.");
                Audit.Add(db, now, Operator, "approval.edited", "invalidated", wf.Id, approval.StepId,
                    metadata: new { fields = decision.Edits?.Keys.ToArray() });
                break;

            default:
                throw new WorkflowRequestException(400, "invalid_decision", "Decision must be approve, reject or edit.");
        }

        await db.SaveChangesAsync(ct);
        events.Publish(new WorkflowEventDto(wf.Id, wf.Status.ToString(), wf.Summary, 0));
        events.SignalWorker();
        return null;
    }

    private async Task ApplyEditsAsync(Approval approval, IReadOnlyDictionary<string, string> edits, CancellationToken ct)
    {
        if (edits.Count == 0) throw new WorkflowRequestException(400, "no_edits", "Nothing was changed.");

        var step = await db.Steps.FirstAsync(s => s.Id == approval.StepId, ct);
        var d = tools.Find(step.ToolId)!.Descriptor;
        var steps = await db.Steps.Where(s => s.WorkflowId == step.WorkflowId && s.Sequence < step.Sequence).ToListAsync(ct);
        var earlier = steps.ToDictionary(s => s.Key, s => tools.Find(s.ToolId)!.Descriptor);
        var folderRefs = (await scope.ListAsync(ct)).Select(FileScope.RefFor).ToHashSet();

        var input = JsonNode.Parse(step.InputJson) as JsonObject ?? [];
        var errors = new List<string>();
        foreach (var (name, value) in edits)
        {
            var p = d.Parameters.FirstOrDefault(p => p.Name == name);
            if (p is not { Editable: true })
            {
                errors.Add($"'{name}' cannot be edited.");
                continue;
            }
            if (string.IsNullOrEmpty(value) && !p.Required)
            {
                input.Remove(name);
                continue;
            }
            JsonNode node = JsonValue.Create(value)!;
            PlanValidator.ValidateValue("edit", p, node, earlier, folderRefs, errors);
            input[name] = node;
        }
        if (errors.Count > 0) throw new WorkflowRequestException(400, "edit_invalid", "Some edits are not valid.", errors);
        step.ReplaceInput(input.ToJsonString());
    }

    public async Task CancelAsync(Guid id, CancellationToken ct)
    {
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new WorkflowRequestException(404, "not_found", "No such workflow.");
        if (wf.Status is WorkflowStatus.Succeeded or WorkflowStatus.Cancelled or WorkflowStatus.Failed)
            throw new WorkflowRequestException(409, "finished", $"This workflow already {wf.Status.ToString().ToLowerInvariant()}.");

        wf.RequestCancel(clock.UtcNow);
        Audit.Add(db, clock.UtcNow, Operator, "workflow.cancel_requested", "requested", wf.Id);
        await db.SaveChangesAsync(ct);
        running.Cancel(id);      // stops an in-flight step
        events.SignalWorker();   // the engine finishes the cancellation and cleans staging
    }

    public async Task RetryAsync(Guid id, CancellationToken ct)
    {
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new WorkflowRequestException(404, "not_found", "No such workflow.");
        if (wf.Status != WorkflowStatus.Failed)
            throw new WorkflowRequestException(409, "not_failed", "Only failed workflows can be retried.");

        foreach (var s in await db.Steps.Where(s => s.WorkflowId == id && (s.Status == StepStatus.Failed || s.Status == StepStatus.Cancelled)).ToListAsync(ct))
            s.ResetForRetry();
        wf.TransitionTo(WorkflowStatus.Pending, clock.UtcNow);
        Audit.Add(db, clock.UtcNow, Operator, "workflow.retry", "pending", wf.Id);
        await db.SaveChangesAsync(ct);
        events.SignalWorker();
    }

    // ---- mapping -------------------------------------------------------------------------

    private static WorkflowSummaryDto Summary(Workflow w, int pending, int steps) => new(
        w.Id, w.Summary, w.RequestText, w.Status.ToString(), w.CreatedAtUtc, w.UpdatedAtUtc, w.CompletedAtUtc,
        w.FailureCode, w.FailureSummary, pending, steps);

    private StepDto ToDto(WorkflowStep s) => new(
        s.Id, s.Sequence, s.Key, s.ToolId, s.Purpose, s.Effect.ToString(),
        tools.Find(s.ToolId)?.Descriptor.RequiresApproval ?? true,
        s.Status.ToString(), TraceJson(s.InputJson), s.OutputJson is null ? null : TraceJson(s.OutputJson),
        s.StartedAtUtc, s.CompletedAtUtc, s.AttemptCount, s.ErrorCode, s.ErrorSummary);

    private ApprovalDto ToDto(Approval a, Workflow wf, IReadOnlyList<WorkflowStep> steps, IReadOnlyList<AllowedFolder> folders)
    {
        var preview = JsonSerializer.Deserialize<StoredPreview>(a.PreviewJson) ?? new StoredPreview([], null, []);
        var step = steps.FirstOrDefault(s => s.Id == a.StepId);
        var editable = new List<EditableFieldDto>();
        if (step is not null && tools.Find(step.ToolId) is { } tool)
        {
            var input = JsonNode.Parse(step.InputJson) as JsonObject ?? [];
            foreach (var p in tool.Descriptor.Parameters.Where(p => p.Editable))
            {
                var value = input[p.Name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                var opts = p.Type == ParamType.FolderRef
                    ? folders.Select(f => new OptionDto(FileScope.RefFor(f), f.Path)).ToList()
                    : null;
                editable.Add(new EditableFieldDto(p.Name, p.Description, p.Type.ToString(), value, opts));
            }
        }
        return new ApprovalDto(a.Id, a.WorkflowId, a.StepId, wf.Summary, a.Title, a.TargetDescription, a.Status.ToString(),
            a.RequestedAtUtc, a.ExpiresAtUtc, a.DecidedAtUtc, a.DecidedBy, a.Note,
            preview.Fields, preview.Content, preview.Warnings, editable);
    }

    /// <summary>Trace view of step data: long text is shortened and secret-looking keys are hidden (FR-028).</summary>
    internal static JsonNode? TraceJson(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        return Redact(node);
    }

    private static readonly string[] SecretMarkers = ["token", "password", "secret", "authorization", "apikey", "api_key"];

    private static JsonNode? Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (k, v) in o)
                    copy[k] = SecretMarkers.Any(m => k.Contains(m, StringComparison.OrdinalIgnoreCase)) ? "[redacted]" : Redact(v);
                return copy;
            case JsonArray arr:
                return new JsonArray(arr.Select(Redact).ToArray());
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 2000:
                return JsonValue.Create(s[..2000] + $"… ({s.Length} characters)");
            default:
                return node?.DeepClone();
        }
    }

    private static string? Trim(string? s, int max) => s is { Length: > 0 } ? (s.Length > max ? s[..max] : s) : null;
}
