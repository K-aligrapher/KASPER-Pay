using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kasper.Application;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;

namespace Kasper.Infrastructure.Notion;

public sealed record NotionSettings(string Token, string DatabaseId, string DatabaseTitle, string TitleProperty);

/// <summary>
/// Notion credentials, encrypted with Windows DPAPI for the current user (FR-007).
/// Never logged, never returned by the API.
/// </summary>
public sealed partial class NotionSettingsStore : IIntegrationStatus
{
    private static string FilePath => Path.Combine(KasperPaths.Root, "secrets", "notion.bin");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KASPER.notion.v1");

    public bool IsConnected(string integration) => integration == "notion" && File.Exists(FilePath);

    public NotionSettings? Load()
    {
        if (!File.Exists(FilePath) || !OperatingSystem.IsWindows()) return null;
        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<NotionSettings>(json);
        }
        catch (CryptographicException) { return null; }
    }

    public void Save(NotionSettings s)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Secrets need Windows DPAPI.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(s), Entropy, DataProtectionScope.CurrentUser));
    }

    public void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    [GeneratedRegex("([0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12})")]
    private static partial Regex IdPattern();

    /// <summary>Accepts a database URL or id; returns the 32-hex id or null.</summary>
    public static string? ParseId(string urlOrId)
    {
        var path = urlOrId.Split('?')[0];
        var matches = IdPattern().Matches(path);
        return matches.Count == 0 ? null : matches[^1].Value.Replace("-", "").ToLowerInvariant();
    }
}

public sealed record NotionPage(string Id, string Title, string Url, string Type);

/// <summary>Minimal Notion REST adapter (API version 2022-06-28). Errors are classified for retries.</summary>
public sealed class NotionClient(HttpClient http)
{
    private const string Version = "2022-06-28";

    private HttpRequestMessage Request(HttpMethod method, string path, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, "https://api.notion.com/v1/" + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Notion-Version", Version);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private async Task<JsonObject> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex)
        {
            throw new ToolException("notion_unreachable", "Can't reach Notion. Check your internet connection.", ErrorCategory.Transient, ex);
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode) return JsonNode.Parse(text) as JsonObject ?? [];
            var message = (JsonNode.Parse(text) as JsonObject)?["message"]?.GetValue<string>() ?? "";
            throw res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new ToolException("notion_unauthorized", "Notion rejected the token. Reconnect Notion in Integrations.", ErrorCategory.Authorization),
                HttpStatusCode.Forbidden or HttpStatusCode.NotFound => new ToolException("notion_not_shared",
                    "KASPER can't see that Notion page/database. In Notion, open it → ••• → Connections → add your KASPER integration.", ErrorCategory.Authorization),
                HttpStatusCode.TooManyRequests => new ToolException("notion_rate_limited", "Notion is rate limiting; KASPER will retry.", ErrorCategory.RateLimit),
                >= HttpStatusCode.InternalServerError => new ToolException("notion_error", "Notion had a temporary problem.", ErrorCategory.Transient),
                _ => new ToolException("notion_bad_request", $"Notion refused the request: {message}", ErrorCategory.Permanent),
            };
        }
    }

    public async Task<string> WhoAmIAsync(string token, CancellationToken ct)
    {
        var me = await SendAsync(Request(HttpMethod.Get, "users/me", token), ct);
        return me["bot"]?["workspace_name"]?.GetValue<string>() ?? me["name"]?.GetValue<string>() ?? "Notion";
    }

    /// <summary>Returns the database title and the name of its title property.</summary>
    public async Task<(string Title, string TitleProperty)> DatabaseAsync(string token, string databaseId, CancellationToken ct)
    {
        var db = await SendAsync(Request(HttpMethod.Get, $"databases/{databaseId}", token), ct);
        var title = string.Concat((db["title"] as JsonArray ?? []).Select(t => t?["plain_text"]?.GetValue<string>()));
        var prop = (db["properties"] as JsonObject ?? []).FirstOrDefault(p => p.Value?["type"]?.GetValue<string>() == "title").Key
            ?? throw new ToolException("notion_no_title", "That database has no title column.", ErrorCategory.Permanent);
        return (string.IsNullOrWhiteSpace(title) ? "Untitled" : title, prop);
    }

    public async Task<IReadOnlyList<NotionPage>> SearchAsync(string token, string query, CancellationToken ct)
    {
        var res = await SendAsync(Request(HttpMethod.Post, "search", token, new { query, page_size = 10 }), ct);
        return (res["results"] as JsonArray ?? []).OfType<JsonObject>().Select(r => new NotionPage(
            r["id"]!.GetValue<string>(), TitleOf(r), r["url"]?.GetValue<string>() ?? "", r["object"]?.GetValue<string>() ?? "page")).ToList();
    }

    /// <summary>Finds pages in the database whose title equals <paramref name="title"/>, created since <paramref name="since"/>.</summary>
    public async Task<NotionPage?> FindRecentAsync(NotionSettings s, string title, DateTime since, CancellationToken ct)
    {
        var res = await SendAsync(Request(HttpMethod.Post, $"databases/{s.DatabaseId}/query", s.Token, new
        {
            filter = new { property = s.TitleProperty, title = new { equals = title } },
            page_size = 5,
        }), ct);
        return (res["results"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(r => DateTime.TryParse(r["created_time"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var c) && c >= since.AddMinutes(-1))
            .Select(r => new NotionPage(r["id"]!.GetValue<string>(), title, r["url"]?.GetValue<string>() ?? "", "page"))
            .FirstOrDefault();
    }

    public async Task<NotionPage> CreateTaskAsync(NotionSettings s, string title, string? notes, CancellationToken ct)
    {
        var props = new JsonObject
        {
            [s.TitleProperty] = new JsonObject { ["title"] = new JsonArray(new JsonObject { ["text"] = new JsonObject { ["content"] = title } }) },
        };
        var body = new JsonObject { ["parent"] = new JsonObject { ["database_id"] = s.DatabaseId }, ["properties"] = props };
        if (!string.IsNullOrWhiteSpace(notes))
        {
            var blocks = new JsonArray();
            foreach (var para in notes.Split("\n\n").Take(50))
                blocks.Add(new JsonObject
                {
                    ["object"] = "block", ["type"] = "paragraph",
                    ["paragraph"] = new JsonObject { ["rich_text"] = new JsonArray(new JsonObject { ["text"] = new JsonObject { ["content"] = para.Length > 1900 ? para[..1900] : para } }) },
                });
            body["children"] = blocks;
        }
        var page = await SendAsync(Request(HttpMethod.Post, "pages", s.Token, body), ct);
        return new NotionPage(page["id"]!.GetValue<string>(), title, page["url"]?.GetValue<string>() ?? "", "page");
    }

    /// <summary>A page from a Notion link/id, or the newest task in the database with that title.</summary>
    public async Task<JsonObject> ResolvePageAsync(NotionSettings s, string pageRef, CancellationToken ct)
    {
        pageRef = pageRef.Trim();
        var looksLikeLink = pageRef.Contains("notion.so", StringComparison.OrdinalIgnoreCase) || pageRef.Contains("notion.site", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(pageRef, "^[0-9a-fA-F-]{32,36}$");
        if (looksLikeLink && NotionSettingsStore.ParseId(pageRef) is { } id)
            return await SendAsync(Request(HttpMethod.Get, $"pages/{id}", s.Token), ct);
        foreach (var filter in new object[] { new { equals = pageRef }, new { contains = pageRef } })
        {
            var res = await SendAsync(Request(HttpMethod.Post, $"databases/{s.DatabaseId}/query", s.Token, new
            {
                filter = new { property = s.TitleProperty, title = filter },
                sorts = new[] { new { timestamp = "created_time", direction = "descending" } },
                page_size = 2,
            }), ct);
            if ((res["results"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault() is { } hit) return hit;
        }
        throw new ToolException("notion_page_not_found", $"No Notion task called “{pageRef}” in {s.DatabaseTitle}. Use the exact title or paste the page link.", ErrorCategory.Validation);
    }

    public static NotionPage AsPage(JsonObject page) =>
        new(page["id"]!.GetValue<string>(), TitleOf(page), page["url"]?.GetValue<string>() ?? "", "page");

    /// <summary>The page's status-like property: a status/select named like "status", any status, or a checkbox (e.g. Done).</summary>
    public static (string Name, string Type, string Current)? StatusProperty(JsonObject page)
    {
        var props = (page["properties"] as JsonObject ?? []).Select(p => (Name: p.Key, Type: p.Value?["type"]?.GetValue<string>() ?? "", Node: p.Value)).ToList();
        var pick = props.FirstOrDefault(p => p.Type is "status" or "select" && p.Name.Contains("status", StringComparison.OrdinalIgnoreCase));
        if (pick.Name is null) pick = props.FirstOrDefault(p => p.Type == "status");
        if (pick.Name is null) pick = props.FirstOrDefault(p => p.Type == "checkbox");
        if (pick.Name is null) return null;
        var current = pick.Type == "checkbox"
            ? (pick.Node?["checkbox"]?.GetValue<bool>() == true ? "Done" : "Not done")
            : pick.Node?[pick.Type]?["name"]?.GetValue<string>() ?? "(empty)";
        return (pick.Name, pick.Type, current);
    }

    public async Task UpdatePageAsync(NotionSettings s, JsonObject page, string? status, string? newTitle, CancellationToken ct)
    {
        var props = new JsonObject();
        if (!string.IsNullOrWhiteSpace(newTitle))
            props[s.TitleProperty] = new JsonObject { ["title"] = new JsonArray(new JsonObject { ["text"] = new JsonObject { ["content"] = newTitle } }) };
        if (!string.IsNullOrWhiteSpace(status))
        {
            var sp = StatusProperty(page) ?? throw new ToolException("notion_no_status", "This task has no Status (or checkbox) column to change.", ErrorCategory.Validation);
            props[sp.Name] = sp.Type == "checkbox"
                ? new JsonObject { ["checkbox"] = status.Trim().ToLowerInvariant() is "done" or "complete" or "completed" or "yes" or "true" or "checked" }
                : new JsonObject { [sp.Type] = new JsonObject { ["name"] = status.Trim() } };
        }
        if (props.Count == 0) throw new ToolException("nothing_to_update", "Give a new status or title.", ErrorCategory.Validation);
        await SendAsync(Request(HttpMethod.Patch, $"pages/{page["id"]!.GetValue<string>()}", s.Token, new JsonObject { ["properties"] = props }), ct);
    }

    private static JsonArray Paragraphs(string text)
    {
        var blocks = new JsonArray();
        foreach (var para in text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Take(90))
            blocks.Add(new JsonObject
            {
                ["object"] = "block", ["type"] = "paragraph",
                ["paragraph"] = new JsonObject { ["rich_text"] = new JsonArray(new JsonObject { ["text"] = new JsonObject { ["content"] = para.Length > 1900 ? para[..1900] : para } }) },
            });
        return blocks;
    }

    public async Task AppendAsync(string token, string pageId, string text, CancellationToken ct) =>
        await SendAsync(Request(HttpMethod.Patch, $"blocks/{pageId}/children", token, new JsonObject { ["children"] = Paragraphs(text) }), ct);

    /// <summary>Plain text of the page's last blocks (used to avoid appending twice on retry).</summary>
    public async Task<string> LastBlocksTextAsync(string token, string pageId, CancellationToken ct)
    {
        var res = await SendAsync(Request(HttpMethod.Get, $"blocks/{pageId}/children?page_size=100", token), ct);
        return string.Join("\n\n", (res["results"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(b => b[b["type"]?.GetValue<string>() ?? ""]?["rich_text"] as JsonArray)
            .Where(rt => rt is not null)
            .Select(rt => string.Concat(rt!.Select(t => t?["plain_text"]?.GetValue<string>() ?? t?["text"]?["content"]?.GetValue<string>()))));
    }

    private static string TitleOf(JsonObject r)
    {
        if (r["title"] is JsonArray dbTitle) return string.Concat(dbTitle.Select(t => t?["plain_text"]?.GetValue<string>()));
        foreach (var (_, p) in r["properties"] as JsonObject ?? [])
            if (p?["type"]?.GetValue<string>() == "title")
                return string.Concat((p["title"] as JsonArray ?? []).Select(t => t?["plain_text"]?.GetValue<string>()));
        return "Untitled";
    }
}
