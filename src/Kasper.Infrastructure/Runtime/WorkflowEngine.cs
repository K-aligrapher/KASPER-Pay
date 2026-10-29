using System.Text.Json;
using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Approvals;
using Kasper.Application.Contracts;
using Kasper.Application.Options;
using Kasper.Application.Planning;
using Kasper.Application.Tools;
using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Files;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Runtime;

/// <summary>
/// Moves one workflow forward as far as it can: runs steps, retries transient failures,
/// stops at Final Review, and re-checks every approval right before acting (NFR-007).
/// </summary>
public sealed class WorkflowEngine(
    KasperDbContext db,
    IToolRegistry tools,
    IClock clock,
    IWorkflowEvents events,
    IDesktopShell shell,
    IOptionsMonitor<RuntimeOptions> options,
    ILogger<WorkflowEngine> log)
{
    private const string System = "kasper";

    public async Task AdvanceAsync(Guid workflowId, CancellationToken ct)
    {
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == workflowId, CancellationToken.None);
        if (wf is null || wf.Status is WorkflowStatus.Succeeded or WorkflowStatus.Cancelled or WorkflowStatus.Failed) return;

        var steps = await db.Steps.Where(s => s.WorkflowId == wf.Id).OrderBy(s => s.Sequence).ToListAsync(CancellationToken.None);
        var approvals = await db.Approvals.Where(a => a.WorkflowId == wf.Id).ToListAsync(CancellationToken.None);

        if (wf.CancelRequested)
        {
            await CancelAsync(wf, steps, approvals, "Cancelled by you.");
            return;
        }

        if (wf.Status == WorkflowStatus.WaitingForApproval)
        {
            var latest = LatestPerStep(approvals);
            if (latest.Any(a => a.Status == ApprovalStatus.Rejected))
            {
                await CancelAsync(wf, steps, approvals, "Rejected in Final Review.");
                return;
            }
            if (latest.Any(a => a.Status == ApprovalStatus.Pending && !a.IsExpired(clock.UtcNow)))
                return; // still waiting for the user
        }

        if (wf.Status != WorkflowStatus.Running && !await TryTransitionAsync(wf, WorkflowStatus.Running)) return;

        try
        {
            foreach (var step in steps.Where(s => !s.IsDone))
            {
                ct.ThrowIfCancellationRequested();

                if (tools.Find(step.ToolId) is not { } tool)
                {
                    await FailAsync(wf, step, "tool_missing", $"The tool '{step.ToolId}' is not available.");
                    return;
                }

                var resolved = Resolve(step, steps);
                if (resolved is null)
                {
                    await FailAsync(wf, step, "reference_unresolved", "An earlier step did not produce the output this step needs.");
                    return;
                }

                if (tool.Descriptor.RequiresApproval)
                {
                    var gate = await CheckApprovalAsync(wf, step, tool, resolved, steps, approvals, ct);
                    if (gate == Gate.Wait)
                    {
                        await WaitForApprovalAsync(wf, approvals);
                        return;
                    }
                    if (gate == Gate.Rejected)
                    {
                        await CancelAsync(wf, steps, approvals, "Rejected in Final Review.");
                        return;
                    }
                }

                if (!await ExecuteStepAsync(wf, step, tool, resolved, ct))
                {
                    await FinishFailedAsync(wf, step);
                    return;
                }
            }

            await TryTransitionAsync(wf, WorkflowStatus.Succeeded);
            ArtifactStore.DeleteStaging(wf.Id);
            shell.Notify("KASPER finished", wf.Summary, "history");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var now = clock.UtcNow;
            foreach (var s in steps.Where(s => s.Status == StepStatus.Running)) s.Cancel(now);
            await CancelAsync(wf, steps, approvals, "Cancelled by you.");
        }
    }

    private enum Gate { Proceed, Wait, Rejected }

    private async Task<Gate> CheckApprovalAsync(Workflow wf, WorkflowStep step, ITool tool, JsonObject resolved,
        List<WorkflowStep> steps, List<Approval> approvals, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var hash = await HashAsync(tool.Descriptor, resolved, ct);
        var latest = approvals.Where(a => a.StepId == step.Id).MaxBy(a => a.RequestedAtUtc);

        switch (latest?.Status)
        {
            case ApprovalStatus.Approved when latest.ActionHash == hash:
                Audit.Add(db, now, System, "approval.verified", "ok", wf.Id, step.Id, latest.TargetDescription);
                return Gate.Proceed;
            case ApprovalStatus.Approved:
                latest.Invalidate(now, "The action changed after it was approved.");
                Audit.Add(db, now, System, "approval.invalidated", "hash_mismatch", wf.Id, step.Id);
                break;
            case ApprovalStatus.Rejected:
                return Gate.Rejected;
            case ApprovalStatus.Pending when latest.IsExpired(now):
                latest.Expire(now);
                Audit.Add(db, now, System, "approval.expired", "expired", wf.Id, step.Id);
                break;
            case ApprovalStatus.Pending when latest.ActionHash != hash:
                latest.Invalidate(now, "The action changed while waiting.");
                Audit.Add(db, now, System, "approval.invalidated", "hash_mismatch", wf.Id, step.Id);
                break;
            case ApprovalStatus.Pending:
                return Gate.Wait;
        }

        await RequestApprovalsAsync(wf, step, steps, approvals, ct);
        return Gate.Wait;
    }

    /// <summary>
    /// Creates Final Review requests for this step and every later approval step whose input is
    /// already known, so the user can review them together (FR-047).
    /// </summary>
    private async Task RequestApprovalsAsync(Workflow wf, WorkflowStep first, List<WorkflowStep> steps,
        List<Approval> approvals, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var lifetime = TimeSpan.FromHours(options.CurrentValue.ApprovalLifetimeHours);

        foreach (var step in steps.Where(s => s.Sequence >= first.Sequence && !s.IsDone))
        {
            if (tools.Find(step.ToolId) is not { Descriptor.RequiresApproval: true } tool) continue;
            if (Resolve(step, steps) is not { } resolved) continue;

            var hash = await HashAsync(tool.Descriptor, resolved, ct);
            var existing = approvals.Where(a => a.StepId == step.Id).MaxBy(a => a.RequestedAtUtc);
            if (existing is { Status: ApprovalStatus.Pending or ApprovalStatus.Approved } && existing.ActionHash == hash) continue;

            var preview = await tool.PreviewAsync(new ToolContext(wf.Id, step.Id, step.IdempotencyKey, 0), resolved, ct);
            var approval = Approval.Request(wf.Id, step.Id, hash, preview.Title, preview.Target,
                JsonSerializer.Serialize(new StoredPreview(preview.Fields, preview.Content, preview.Warnings ?? [])),
                resolved.ToJsonString(), now, lifetime);
            db.Approvals.Add(approval);
            approvals.Add(approval);
            step.WaitForApproval();
            Audit.Add(db, now, System, "approval.requested", "pending", wf.Id, step.Id, preview.Target);
        }
    }

    private async Task WaitForApprovalAsync(Workflow wf, List<Approval> approvals)
    {
        if (!await TryTransitionAsync(wf, WorkflowStatus.WaitingForApproval)) return;
        var pending = LatestPerStep(approvals).Count(a => a.Status == ApprovalStatus.Pending);
        shell.Notify("KASPER needs your approval",
            pending == 1 ? wf.Summary : $"{wf.Summary} ({pending} actions to review)", "approvals");
    }

    /// <summary>Runs a step with timeout and bounded retries (FR-023, FR-024, NFR-022).</summary>
    private async Task<bool> ExecuteStepAsync(Workflow wf, WorkflowStep step, ITool tool, JsonObject resolved, CancellationToken ct)
    {
        var d = tool.Descriptor;
        var maxAttempts = Math.Max(1, d.MaxAttempts);

        while (true)
        {
            step.BeginAttempt(clock.UtcNow);
            Audit.Add(db, clock.UtcNow, System, "step.started", "running", wf.Id, step.Id, d.Id, new { attempt = step.AttemptCount });
            await db.SaveChangesAsync(CancellationToken.None); // persist before any side effect (NFR-020)
            Publish(wf);

            string code, message;
            ErrorCategory category;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(d.Timeout);
            var started = clock.UtcNow;
            try
            {
                var result = await tool.ExecuteAsync(new ToolContext(wf.Id, step.Id, step.IdempotencyKey, step.AttemptCount), resolved, timeout.Token);
                step.Succeed(result.Output.ToJsonString(), clock.UtcNow);
                Audit.Add(db, clock.UtcNow, System, "step.succeeded", "ok", wf.Id, step.Id, d.Id,
                    new { attempt = step.AttemptCount, ms = (int)(clock.UtcNow - started).TotalMilliseconds });
                await db.SaveChangesAsync(CancellationToken.None);
                Publish(wf);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                (code, message, category) = ("timeout", $"{d.Id} did not finish within {d.Timeout.TotalSeconds:0}s.", ErrorCategory.Timeout);
            }
            catch (ToolException ex)
            {
                (code, message, category) = (ex.Code, ex.Message, ex.Category);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Tool {Tool} crashed in workflow {WorkflowId}", d.Id, wf.Id);
                (code, message, category) = ("unexpected", "The step failed unexpectedly. See logs for details.", ErrorCategory.Permanent);
            }

            // A timeout does not prove a write failed: only retry it when the tool is idempotent.
            var retryable = category is ErrorCategory.Transient or ErrorCategory.RateLimit
                || (category == ErrorCategory.Timeout && d.Idempotent);
            if (retryable && step.AttemptCount < maxAttempts)
            {
                step.RecordRetryableError(code, message);
                Audit.Add(db, clock.UtcNow, System, "step.retry", category.ToString(), wf.Id, step.Id, d.Id, new { attempt = step.AttemptCount, code });
                await db.SaveChangesAsync(CancellationToken.None);
                var baseMs = options.CurrentValue.RetryBaseDelayMs;
                var delay = baseMs * Math.Pow(2, step.AttemptCount - 1) + Random.Shared.Next(0, Math.Max(1, baseMs));
                await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
                continue;
            }

            step.Fail(code, message, clock.UtcNow);
            Audit.Add(db, clock.UtcNow, System, "step.failed", category.ToString(), wf.Id, step.Id, d.Id, new { attempt = step.AttemptCount, code });
            await db.SaveChangesAsync(CancellationToken.None);
            return false;
        }
    }

    private async Task FinishFailedAsync(Workflow wf, WorkflowStep step)
    {
        await TryTransitionAsync(wf, WorkflowStatus.Failed, step.ErrorCode, $"Step {step.Sequence} ({step.Purpose}): {step.ErrorSummary}");
        shell.Notify("KASPER hit a problem", $"{wf.Summary}: {step.ErrorSummary}", "history");
    }

    private async Task FailAsync(Workflow wf, WorkflowStep step, string code, string message)
    {
        step.Fail(code, message, clock.UtcNow);
        await FinishFailedAsync(wf, step);
    }

    private async Task CancelAsync(Workflow wf, List<WorkflowStep> steps, List<Approval> approvals, string reason)
    {
        var now = clock.UtcNow;
        foreach (var s in steps) s.Cancel(now);
        foreach (var a in approvals.Where(a => a.Status == ApprovalStatus.Pending)) a.Invalidate(now, "Workflow cancelled.");
        if (await TryTransitionAsync(wf, WorkflowStatus.Cancelled, failureSummary: reason))
            ArtifactStore.DeleteStaging(wf.Id); // nothing leaves staging when cancelled or rejected
    }

    /// <summary>Persists a status change; returns false if another process changed the workflow first.</summary>
    private async Task<bool> TryTransitionAsync(Workflow wf, WorkflowStatus next, string? code = null, string? failureSummary = null)
    {
        if (!Domain.Rules.WorkflowStateMachine.CanTransition(wf.Status, next)) return false;
        var from = wf.Status;
        wf.TransitionTo(next, clock.UtcNow, code, failureSummary);
        Audit.Add(db, clock.UtcNow, System, "workflow.status", next.ToString(), wf.Id, metadata: new { from = from.ToString() });
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            log.LogInformation("Workflow {WorkflowId} changed concurrently; skipping.", wf.Id);
            return false;
        }
        Publish(wf);
        return true;
    }

    private void Publish(Workflow wf) =>
        events.Publish(new WorkflowEventDto(wf.Id, wf.Status.ToString(), wf.Summary,
            db.Approvals.Local.Count(a => a.WorkflowId == wf.Id && a.Status == ApprovalStatus.Pending)));

    internal static JsonObject? Resolve(WorkflowStep step, IReadOnlyList<WorkflowStep> steps)
    {
        var input = JsonNode.Parse(step.InputJson) as JsonObject ?? [];
        return StepReferences.Resolve(input, key =>
            steps.FirstOrDefault(s => s.Key == key && s.Status == StepStatus.Succeeded && s.Sequence < step.Sequence) is { OutputJson: { } json }
                ? JsonNode.Parse(json) as JsonObject
                : null);
    }

    /// <summary>Hash over the resolved input plus the current content hash of every file involved.</summary>
    private async Task<string> HashAsync(ToolDescriptor d, JsonObject resolved, CancellationToken ct)
    {
        var fileHashes = new Dictionary<string, string>();
        foreach (var p in d.Parameters.Where(p => p.Type == ParamType.ArtifactRef))
        {
            if (resolved[p.Name] is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var id)
                && await db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, ct) is { } artifact)
            {
                fileHashes[p.Name] = await ArtifactStore.CurrentHashAsync(artifact.StagingPath, ct)
                    ?? (artifact.CommittedPath is not null ? artifact.Sha256 : "missing");
            }
        }
        return ActionHasher.Compute(d.Id, d.Version, resolved, fileHashes);
    }

    internal static List<Approval> LatestPerStep(IEnumerable<Approval> approvals) =>
        approvals.GroupBy(a => a.StepId).Select(g => g.MaxBy(a => a.RequestedAtUtc)!).ToList();
}

internal sealed record StoredPreview(IReadOnlyList<PreviewField> Fields, string? Content, IReadOnlyList<string> Warnings);
