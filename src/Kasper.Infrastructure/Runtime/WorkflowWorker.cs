using Kasper.Application;
using Kasper.Application.Abstractions;
using Kasper.Application.Options;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Files;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Runtime;

/// <summary>
/// Background worker (NFR-041): recovers interrupted workflows at startup, then advances any
/// workflow that has work to do. One workflow at a time keeps a laptop responsive.
/// </summary>
public sealed class WorkflowWorker(
    IServiceScopeFactory scopes,
    IWorkflowEvents events,
    RunningWorkflows running,
    IClock clock,
    IOptionsMonitor<RuntimeOptions> options,
    ILogger<WorkflowWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await RecoverAsync(stoppingToken);
            await CleanOldStagingAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Workflow recovery failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var id in await FindWorkAsync(stoppingToken))
                {
                    var cts = running.Register(id, stoppingToken);
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<WorkflowEngine>().AdvanceAsync(id, cts.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        log.LogError(ex, "Workflow {WorkflowId} could not advance", id);
                    }
                    finally
                    {
                        running.Unregister(id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Workflow worker loop error");
            }

            await events.WaitForSignalAsync(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task<List<Guid>> FindWorkAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KasperDbContext>();
        var now = clock.UtcNow;
        return await db.Workflows
            .Where(w => w.Status == WorkflowStatus.Pending
                || w.Status == WorkflowStatus.Running
                || (w.Status == WorkflowStatus.WaitingForApproval
                    && (w.CancelRequested || !db.Approvals.Any(a => a.WorkflowId == w.Id && a.Status == ApprovalStatus.Pending && a.ExpiresAtUtc > now))))
            .OrderBy(w => w.CreatedAtUtc)
            .Select(w => w.Id)
            .Take(20)
            .ToListAsync(ct);
    }

    /// <summary>
    /// FR-027: a step that was running when KASPER stopped is replayed only if its tool is
    /// idempotent; otherwise the workflow fails with a clear "check before retrying" message.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KasperDbContext>();
        var tools = scope.ServiceProvider.GetRequiredService<IToolRegistry>();
        await scope.ServiceProvider.GetRequiredService<FileScope>().EnsureDefaultAsync(clock.UtcNow, ct);

        var interrupted = await db.Steps.Where(s => s.Status == StepStatus.Running).ToListAsync(ct);
        foreach (var step in interrupted)
        {
            var wf = await db.Workflows.FirstAsync(w => w.Id == step.WorkflowId, ct);
            if (tools.Find(step.ToolId)?.Descriptor.Idempotent == true)
            {
                step.ResetForRetry();
                Audit.Add(db, clock.UtcNow, "kasper", "step.recovered", "replay", wf.Id, step.Id, step.ToolId);
            }
            else
            {
                step.Fail("interrupted", "KASPER stopped while this step was running. Check the target before retrying.", clock.UtcNow);
                if (wf.Status == WorkflowStatus.Running)
                    wf.TransitionTo(WorkflowStatus.Failed, clock.UtcNow, "interrupted", step.ErrorSummary);
                Audit.Add(db, clock.UtcNow, "kasper", "step.recovered", "failed", wf.Id, step.Id, step.ToolId);
            }
        }
        await db.SaveChangesAsync(ct);
        if (interrupted.Count > 0) log.LogInformation("Recovered {Count} interrupted step(s)", interrupted.Count);
    }

    /// <summary>NFR-008: drop staging left by finished/failed workflows after the retention period.</summary>
    private async Task CleanOldStagingAsync(CancellationToken ct)
    {
        if (!Directory.Exists(KasperPaths.Staging)) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KasperDbContext>();
        var active = (await db.Workflows
                .Where(w => w.Status == WorkflowStatus.Pending || w.Status == WorkflowStatus.Running || w.Status == WorkflowStatus.WaitingForApproval)
                .Select(w => w.Id).ToListAsync(ct))
            .Select(id => id.ToString("N")).ToHashSet();

        var cutoff = DateTime.UtcNow.AddDays(-options.CurrentValue.FailedStagingRetentionDays);
        foreach (var dir in Directory.EnumerateDirectories(KasperPaths.Staging))
        {
            if (active.Contains(Path.GetFileName(dir))) continue;
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff) Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
