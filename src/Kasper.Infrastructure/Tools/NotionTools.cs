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

/// <summary>notion.update_task: change a task's status and/or title (asks you first, shows old → new).</summary>
public sealed class NotionUpdateTaskTool(NotionClient notion, NotionSettingsStore store) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "notion.update_task", 1, "Change an existing Notion task's status (e.g. Done, In progress) and/or title (asks you first).",
        EffectClass.ExternalWrite, false,
        [
            new ToolParameter("task", ParamType.Text, true, "The task's current title or its Notion link.", 300),
            new ToolParameter("status", ParamType.Text, false, "New status, e.g. Done.", 60, Editable: true),
            new ToolParameter("newTitle", ParamType.Text, false, "New title.", 200, Editable: true),
        ],
        ["pageId", "url"], TimeSpan.FromSeconds(45), MaxAttempts: 3, Idempotent: true, Integration: "notion");

    public async Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var s = NotionAccess.Settings(store);
        var page = await notion.ResolvePageAsync(s, Input.Str(input, "task"), ct);
        var p = NotionClient.AsPage(page);
        var fields = new List<PreviewField> { new("Task", p.Title), new("Database", s.DatabaseTitle) };
        if (Input.OptStr(input, "status") is { } st)
            fields.Add(new("Status", $"{NotionClient.StatusProperty(page)?.Current ?? "(no status column)"} → {st}"));
        if (Input.OptStr(input, "newTitle") is { } nt) fields.Add(new("New title", nt));
        return new ToolPreview($"Update Notion task: {p.Title}", $"Notion › {s.DatabaseTitle} › {p.Title}", fields, null);
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var s = NotionAccess.Settings(store);
        var page = await notion.ResolvePageAsync(s, Input.Str(input, "task"), ct);
        // Setting the same values twice is harmless, so retries need no reconciliation.
        await notion.UpdatePageAsync(s, page, Input.OptStr(input, "status"), Input.OptStr(input, "newTitle"), ct);
        var p = NotionClient.AsPage(page);
        return new ToolResult(new JsonObject { ["pageId"] = p.Id, ["url"] = p.Url });
    }
}

/// <summary>notion.append: add text to the end of an existing page (asks you first; never adds it twice).</summary>
public sealed class NotionAppendTool(NotionClient notion, NotionSettingsStore store) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "notion.append", 1, "Add notes/text to the end of an existing Notion page or task (asks you first).",
        EffectClass.ExternalWrite, false,
        [
            new ToolParameter("page", ParamType.Text, true, "A task title or a Notion page link.", 300),
            new ToolParameter("text", ParamType.Text, true, "Text to add (blank lines start new paragraphs).", 8000, Editable: true),
        ],
        ["pageId", "url"], TimeSpan.FromSeconds(45), MaxAttempts: 3, Idempotent: true, Integration: "notion");

    public async Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var s = NotionAccess.Settings(store);
        var p = NotionClient.AsPage(await notion.ResolvePageAsync(s, Input.Str(input, "page"), ct));
        return new ToolPreview($"Add to Notion page: {p.Title}", $"Notion › {p.Title}", [new("Page", p.Title), new("Link", p.Url)], Input.Str(input, "text"));
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var s = NotionAccess.Settings(store);
        var p = NotionClient.AsPage(await notion.ResolvePageAsync(s, Input.Str(input, "page"), ct));
        var text = Input.Str(input, "text").Replace("\r\n", "\n").Trim();
        if (context.Attempt > 1)
        {
            // A previous attempt may have succeeded before timing out: don't add the text twice.
            var existing = await notion.LastBlocksTextAsync(s.Token, p.Id, ct);
            var last = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)[^1];
            if (existing.TrimEnd().EndsWith(last.Length > 1900 ? last[..1900] : last, StringComparison.Ordinal))
                return new ToolResult(new JsonObject { ["pageId"] = p.Id, ["url"] = p.Url });
        }
        await notion.AppendAsync(s.Token, p.Id, text, ct);
        return new ToolResult(new JsonObject { ["pageId"] = p.Id, ["url"] = p.Url });
    }
}
