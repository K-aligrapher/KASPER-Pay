using System.Text.Json;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Application.Planning;
using Kasper.Infrastructure.Files;
using Kasper.Infrastructure.Persistence;
using Kasper.Infrastructure.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Kasper.Api.Endpoints;

internal static class WorkflowEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        // Command bar / CLI: answer, or plan and start a workflow.
        app.MapPost("/api/assistant", (AssistantRequestDto request, AssistantService assistant, CancellationToken ct) =>
            Guard(async () =>
            {
                if (request.Messages is not { Count: > 0 and <= 50 } ||
                    request.Messages.Any(m => m.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(m.Content) || m.Content.Length > 16_000))
                    return Results.BadRequest(new ErrorDto("invalid_request", "Send 1–50 user/assistant messages of up to 16000 characters."));
                return Results.Ok(await assistant.HandleAsync(request.Messages, ct));
            }));

        app.MapPost("/api/workflows", (CreateWorkflowDto dto, WorkflowService workflows, IPlanner planner, CancellationToken ct) =>
            Guard(async () =>
            {
                Plan plan;
                if (dto.Plan is { } p)
                {
                    plan = new Plan(p.Summary, p.Steps.Select(s => new PlanStep(s.Id, s.Tool, s.Purpose ?? "", s.Input)).ToList());
                }
                else
                {
                    var result = await planner.PlanAsync([new ChatMessageDto("user", dto.Request)], ct);
                    if (result.Plan is null)
                        return Results.UnprocessableEntity(new ErrorDto("no_plan", result.Error ?? result.Reply ?? "No tools were needed."));
                    plan = result.Plan;
                }
                var created = await workflows.CreateAsync(dto.Request, plan, ct);
                return Results.Created($"/api/workflows/{created.Id}", created);
            }));

        app.MapGet("/api/workflows", (int? take, WorkflowService workflows, CancellationToken ct) =>
            Guard(async () => Results.Ok(await workflows.ListAsync(take ?? 50, ct))));

        app.MapGet("/api/workflows/{id:guid}", (Guid id, WorkflowService workflows, CancellationToken ct) =>
            Guard(async () => Results.Ok(await workflows.GetAsync(id, ct))));

        app.MapPost("/api/workflows/{id:guid}/cancel", (Guid id, WorkflowService workflows, CancellationToken ct) =>
            Guard(async () =>
            {
                await workflows.CancelAsync(id, ct);
                return Results.Accepted();
            }));

        app.MapPost("/api/workflows/{id:guid}/retry", (Guid id, WorkflowService workflows, CancellationToken ct) =>
            Guard(async () =>
            {
                await workflows.RetryAsync(id, ct);
                return Results.Accepted();
            }));

        app.MapGet("/api/approvals", (WorkflowService workflows, CancellationToken ct) =>
            Guard(async () => Results.Ok(await workflows.PendingApprovalsAsync(ct))));

        app.MapPost("/api/approvals/{id:guid}/decision", (Guid id, DecisionDto decision, WorkflowService workflows, CancellationToken ct) =>
            Guard(async () =>
            {
                await workflows.DecideAsync(id, decision, ct);
                return Results.NoContent();
            }));

        // Live updates (SSE). Uses the bearer token, so the UI reads it with fetch() streaming.
        app.MapGet("/api/events", async (HttpContext http, IWorkflowEvents events, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            await http.Response.WriteAsync(": connected\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
            try
            {
                await foreach (var e in events.SubscribeAsync(ct))
                {
                    await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(e, Json)}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
        });

        app.MapGet("/api/tools", (Kasper.Application.Tools.IToolRegistry tools) =>
            tools.Available.Select(t => new
            {
                t.Id, t.Description, Effect = t.Effect.ToString(), t.RequiresApproval,
                Inputs = t.Parameters.Select(p => p.Name), t.Outputs,
            }));

        // Allowed folders (FR-005). Only the user can change these; no tool can.
        app.MapGet("/api/settings/folders", (FileScope scope, IClock clock, CancellationToken ct) =>
            Guard(async () =>
            {
                await scope.EnsureDefaultAsync(clock.UtcNow, ct);
                return Results.Ok((await scope.ListAsync(ct))
                    .Select(f => new FolderDto(f.Id, FileScope.RefFor(f), f.Path, f.IsDefault, Directory.Exists(f.Path))));
            }));

        app.MapPost("/api/settings/folders", (AddFolderDto dto, KasperDbContext db, IClock clock, CancellationToken ct) =>
            Guard(async () =>
            {
                if (FileScope.FolderProblem(dto.Path ?? "") is { } problem)
                    return Results.BadRequest(new ErrorDto("folder_invalid", problem));
                var path = FileScope.Normalize(dto.Path!);
                if (await db.AllowedFolders.AnyAsync(f => f.Path == path, ct))
                    return Results.Conflict(new ErrorDto("folder_exists", "That folder is already allowed."));
                var folder = new Kasper.Domain.Entities.AllowedFolder { Path = path, AddedAtUtc = clock.UtcNow };
                db.AllowedFolders.Add(folder);
                db.AuditEvents.Add(new Kasper.Domain.Entities.AuditEvent
                {
                    Actor = WorkflowService.Operator, EventType = "settings.folder_added", Outcome = "ok", Target = path, TimestampUtc = clock.UtcNow,
                });
                await db.SaveChangesAsync(ct);
                return Results.Ok(new FolderDto(folder.Id, FileScope.RefFor(folder), folder.Path, false, true));
            }));

        app.MapDelete("/api/settings/folders/{id:int}", (int id, KasperDbContext db, IClock clock, CancellationToken ct) =>
            Guard(async () =>
            {
                var folder = await db.AllowedFolders.FirstOrDefaultAsync(f => f.Id == id, ct);
                if (folder is null) return Results.NotFound(new ErrorDto("not_found", "No such folder."));
                if (folder.IsDefault) return Results.BadRequest(new ErrorDto("folder_default", "The default KASPER folder cannot be removed."));
                db.AllowedFolders.Remove(folder);
                db.AuditEvents.Add(new Kasper.Domain.Entities.AuditEvent
                {
                    Actor = WorkflowService.Operator, EventType = "settings.folder_removed", Outcome = "ok", Target = folder.Path, TimestampUtc = clock.UtcNow,
                });
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            }));
    }

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (WorkflowRequestException ex)
        {
            return Results.Json(new ErrorDto(ex.Code, ex.Message, ex.Details.Count > 0 ? ex.Details : null), statusCode: ex.Status);
        }
        catch (ChatModelException ex)
        {
            return Results.Json(new ErrorDto(ex.Code, ex.Message), statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
