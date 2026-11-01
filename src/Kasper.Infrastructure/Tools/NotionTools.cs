using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Notion;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kasper.Infrastructure.Tools;

internal static class NotionAccess
{
    public static NotionSettings Settings(NotionSettingsStore store) => store.Load()
        ?? throw new ToolException("notion_not_connected", "Notion is not connected. Connect it in Integrations.", ErrorCategory.Authorization);
}

/// <summary>notion.search: find pages/databases KASPER's integration can see. Read-only.</summary>
public sealed class NotionSearchTool(NotionClient notion, NotionSettingsStore store) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "notion.search", 1, "Search Notion pages shared with KASPER.", EffectClass.Read, true,
        [new ToolParameter("query", ParamType.Text, true, "Words to search for.", 200)],
        ["text", "count"], TimeSpan.FromSeconds(30), MaxAttempts: 3, Idempotent: true, Integration: "notion");

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var s = NotionAccess.Settings(store);
        var pages = await notion.SearchAsync(s.Token, Input.Str(input, "query"), ct);
        // Titles and links only: page content is untrusted and stays out of the plan.
        var text = pages.Count == 0 ? "No matching Notion pages." : string.Join("\n", pages.Select(p => $"- {p.Title} ({p.Url})"));
        return new ToolResult(new JsonObject { ["text"] = text, ["count"] = pages.Count });
    }
}

/// <summary>
/// notion.create_task: adds a page to the connected task database. Always goes through Final Review.
/// On replay it looks for the page first, so a retry never creates a duplicate (FR-057).
/// </summary>
public sealed class NotionCreateTaskTool(NotionClient notion, NotionSettingsStore store, KasperDbContext db) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "notion.create_task", 1, "Create a task in your Notion task database (asks you first).",
        EffectClass.ExternalWrite, false,
        [
            new ToolParameter("title", ParamType.Text, true, "Task title.", 200, Editable: true),
            new ToolParameter("notes", ParamType.Text, false, "Details for the task body.", 4000, Editable: true),
        ],
        ["pageId", "url"], TimeSpan.FromSeconds(45), MaxAttempts: 3, Idempotent: true, Integration: "notion");

    public Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var s = NotionAccess.Settings(store);
        return Task.FromResult(new ToolPreview(
            $"Create Notion task: {Input.Str(input, "title")}",
            $"Notion › {s.DatabaseTitle}",
            [new PreviewField("Database", s.DatabaseTitle), new PreviewField("Title", Input.Str(input, "title"))],
            Input.OptStr(input, "notes")));
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var s = NotionAccess.Settings(store);
        var title = Input.Str(input, "title");

        if (context.Attempt > 1)
        {
            // A previous attempt may have succeeded before a timeout or crash: reconcile first.
            var started = await db.Steps.Where(x => x.Id == context.StepId).Select(x => x.StartedAtUtc).FirstOrDefaultAsync(ct) ?? DateTime.UtcNow.AddHours(-1);
            if (await notion.FindRecentAsync(s, title, started, ct) is { } existing)
                return Result(existing);
        }
        return Result(await notion.CreateTaskAsync(s, title, Input.OptStr(input, "notes"), ct));
    }

    private static ToolResult Result(NotionPage p) => new(new JsonObject { ["pageId"] = p.Id, ["url"] = p.Url });
}
