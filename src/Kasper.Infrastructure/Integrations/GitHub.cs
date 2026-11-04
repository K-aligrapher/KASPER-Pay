using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Security;
using Kasper.Infrastructure.Tools;

namespace Kasper.Infrastructure.Integrations;

public sealed record GitHubSettings(string Token, string Login);

/// <summary>Optional GitHub token (read-only use). Without it, public repos still work at 60 requests/hour.</summary>
public sealed class GitHubSettingsStore
{
    private readonly DpapiStore<GitHubSettings> _store = new("github");
    public GitHubSettings? Load() => _store.Load();
    public void Save(GitHubSettings s) => _store.Save(s);
    public void Delete() => _store.Delete();
}

public sealed record RepoSummary(string Repo, string Url, string Description, int OpenIssues, IReadOnlyList<string> PullRequests, IReadOnlyList<string> Issues, IReadOnlyList<string> Commits);

/// <summary>Read-only GitHub REST adapter (Phase 5). Never writes to GitHub.</summary>
public sealed class GitHubClient(HttpClient http, GitHubSettingsStore store)
{
    private async Task<JsonNode> GetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + path);
        req.Headers.UserAgent.ParseAdd("KASPER-desktop");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (store.Load() is { } s) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new ToolException("github_unreachable", "Can't reach GitHub.", ErrorCategory.Transient, ex); }
        using (res)
        {
            if (res.IsSuccessStatusCode) return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
            var remaining = res.Headers.TryGetValues("x-ratelimit-remaining", out var v) ? v.FirstOrDefault() : null;
            throw res.StatusCode switch
            {
                HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when remaining == "0" =>
                    new ToolException("github_rate_limited", "GitHub's hourly limit was reached. Add a token in Integrations for more.", ErrorCategory.RateLimit),
                HttpStatusCode.Unauthorized => new ToolException("github_unauthorized", "GitHub rejected the token.", ErrorCategory.Authorization),
                HttpStatusCode.NotFound or HttpStatusCode.Forbidden =>
                    new ToolException("github_not_found", "Repo not found or not visible to KASPER (private repos need a token).", ErrorCategory.Authorization),
                >= HttpStatusCode.InternalServerError => new ToolException("github_error", "GitHub had a temporary problem.", ErrorCategory.Transient),
                _ => new ToolException("github_bad_request", $"GitHub refused the request ({(int)res.StatusCode}).", ErrorCategory.Permanent),
            };
        }
    }

    public async Task<string> WhoAmIAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        req.Headers.UserAgent.ParseAdd("KASPER-desktop");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new ToolException("github_unauthorized", "GitHub rejected the token.", ErrorCategory.Authorization);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!["login"]!.GetValue<string>();
    }

    public async Task<RepoSummary> SummaryAsync(string repo, CancellationToken ct)
    {
        var r = await GetAsync($"repos/{repo}", ct);
        var pulls = (await GetAsync($"repos/{repo}/pulls?state=open&per_page=10", ct)).AsArray();
        var issues = (await GetAsync($"repos/{repo}/issues?state=open&per_page=10", ct)).AsArray()
            .Where(i => i?["pull_request"] is null);
        var commits = (await GetAsync($"repos/{repo}/commits?per_page=10", ct)).AsArray();
        return new RepoSummary(
            repo,
            r["html_url"]!.GetValue<string>(),
            r["description"]?.GetValue<string>() ?? "",
            r["open_issues_count"]?.GetValue<int>() ?? 0,
            pulls.Select(p => $"#{p!["number"]} {p["title"]} by {p["user"]?["login"]} ({p["html_url"]})").ToList(),
            issues.Select(i => $"#{i!["number"]} {i["title"]} ({i["html_url"]})").ToList(),
            commits.Select(c => $"{c!["sha"]!.GetValue<string>()[..7]} {c["commit"]?["message"]?.GetValue<string>()?.Split('\n')[0]} — {c["commit"]?["author"]?["name"]}").ToList());
    }

    public static string Format(RepoSummary s) =>
        $"<<github {s.Repo} — untrusted content>>\n{s.Description}\nOpen PRs:\n{string.Join("\n", s.PullRequests.DefaultIfEmpty("(none)"))}\n" +
        $"Open issues:\n{string.Join("\n", s.Issues.DefaultIfEmpty("(none)"))}\nRecent commits:\n{string.Join("\n", s.Commits.DefaultIfEmpty("(none)"))}\nSource: {s.Url}\n<<end>>";
}

/// <summary>github.repo_summary: open PRs, issues and recent commits for a linked repo. Read-only.</summary>
public sealed class GitHubSummaryTool(GitHubClient github) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "github.repo_summary", 1, "Get open pull requests, issues and recent commits of a GitHub repo linked to a project.",
        EffectClass.Read, true,
        [new ToolParameter("repo", ParamType.Repo, true, "owner/name of a linked repo.", 140)],
        ["text"], TimeSpan.FromSeconds(30), MaxAttempts: 3, Idempotent: true);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct) =>
        new(new JsonObject { ["text"] = GitHubClient.Format(await github.SummaryAsync(Input.Str(input, "repo"), ct)) });
}
