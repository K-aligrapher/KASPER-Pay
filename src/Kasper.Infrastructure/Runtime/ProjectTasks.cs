using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Planning;
using Kasper.Application.Tools;
using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Persistence;
using Kasper.Infrastructure.Tools;
using Microsoft.EntityFrameworkCore;

namespace Kasper.Infrastructure.Runtime;

public sealed record ProjectTaskDto(Guid Id, Guid ProjectId, string Title, bool Done, DateTime CreatedAtUtc, DateTime? DoneAtUtc, Guid? NotionWorkflowId);

/// <summary>Local project task list (FR-304). Changes you make yourself in the UI apply directly.</summary>
public sealed class ProjectTaskService(KasperDbContext db, IClock clock)
{
    public static ProjectTaskDto ToDto(ProjectTask t) => new(t.Id, t.ProjectId, t.Title, t.Done, t.CreatedAtUtc, t.DoneAtUtc, t.NotionWorkflowId);

    public async Task<List<ProjectTaskDto>> ListAsync(Guid projectId, CancellationToken ct) =>
        (await db.ProjectTasks.Where(t => t.ProjectId == projectId).ToListAsync(ct))
            .OrderBy(t => t.Done).ThenBy(t => t.CreatedAtUtc).Select(ToDto).ToList();

    private static string CleanTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) || title.Trim().Length > 300
            ? throw new WorkflowRequestException(400, "title_invalid", "Give the task a title (up to 300 characters).")
            : title.Trim();

    public async Task<ProjectTaskDto> AddAsync(Guid projectId, string? title, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new WorkflowRequestException(404, "not_found", "No such project.");
        var t = db.ProjectTasks.Add(new ProjectTask { ProjectId = projectId, Title = CleanTitle(title), CreatedAtUtc = clock.UtcNow }).Entity;
        Audit.Add(db, clock.UtcNow, WorkflowService.Operator, "project.task_added", "ok", target: $"{project.Name}: {t.Title}");
        await db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    public async Task<ProjectTaskDto> UpdateAsync(Guid projectId, Guid taskId, string? title, bool? done, CancellationToken ct)
    {
        var t = await FindAsync(projectId, taskId, ct);
        if (title is not null) t.Title = CleanTitle(title);
        if (done is { } d && d != t.Done)
        {
            t.Done = d;
            t.DoneAtUtc = d ? clock.UtcNow : null;
        }
        await db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    public async Task DeleteAsync(Guid projectId, Guid taskId, CancellationToken ct)
    {
        db.ProjectTasks.Remove(await FindAsync(projectId, taskId, ct));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Starts a workflow that creates the task in Notion. It stops at Final Review like any Notion write.</summary>
    public async Task<ProjectTaskDto> SendToNotionAsync(Guid projectId, Guid taskId, WorkflowService workflows, CancellationToken ct)
    {
        var t = await FindAsync(projectId, taskId, ct);
        var project = await db.Projects.FirstAsync(p => p.Id == projectId, ct);
        var plan = new Plan($"Send “{t.Title}” to Notion",
        [
            new PlanStep("s1", "notion.create_task", "Create the task in Notion",
                new JsonObject { ["title"] = t.Title, ["notes"] = $"Project: {project.Name}" }),
        ]);
        var created = await workflows.CreateAsync($"Project {project.Name}: send task to Notion", plan, ct);
        t.NotionWorkflowId = created.Id;
        await db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    private async Task<ProjectTask> FindAsync(Guid projectId, Guid taskId, CancellationToken ct) =>
        await db.ProjectTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.ProjectId == projectId, ct)
        ?? throw new WorkflowRequestException(404, "not_found", "No such task.");
}

/// <summary>project.add_task: lets KASPER add to-dos to a project's list from chat (asks you first).</summary>
public sealed class ProjectAddTaskTool(KasperDbContext db, IClock clock) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "project.add_task", 1, "Add a to-do to one of your KASPER projects' task lists (asks you first).",
        EffectClass.LocalWrite, false,
        [
            new ToolParameter("project", ParamType.Text, true, "Project name.", 100),
            new ToolParameter("title", ParamType.Text, true, "The task.", 300, Editable: true),
        ],
        ["taskId", "project"], TimeSpan.FromSeconds(15), MaxAttempts: 2, Idempotent: true);

    private async Task<Project> ProjectAsync(JsonObject input, CancellationToken ct)
    {
        var name = Input.Str(input, "project").Trim();
        var all = await db.Projects.ToListAsync(ct);
        return all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException("project_not_found", $"No project called “{name}”. Projects: {string.Join(", ", all.Select(p => p.Name))}", ErrorCategory.Validation);
    }

    public async Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var p = await ProjectAsync(input, ct);
        return new ToolPreview($"Add task to {p.Name}", $"KASPER › Projects › {p.Name}", [new("Project", p.Name), new("Task", Input.Str(input, "title"))]);
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var p = await ProjectAsync(input, ct);
        var title = Input.Str(input, "title").Trim();
        // Replay-safe: the same title added in the last hour is the same task.
        var since = clock.UtcNow.AddHours(-1);
        var t = await db.ProjectTasks.FirstOrDefaultAsync(x => x.ProjectId == p.Id && x.Title == title && x.CreatedAtUtc >= since, ct);
        if (t is null)
        {
            t = db.ProjectTasks.Add(new ProjectTask { ProjectId = p.Id, Title = title, CreatedAtUtc = clock.UtcNow }).Entity;
            await db.SaveChangesAsync(ct);
        }
        return new ToolResult(new JsonObject { ["taskId"] = t.Id.ToString(), ["project"] = p.Name });
    }
}
