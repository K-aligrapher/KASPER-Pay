using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kasper.Api;
using Kasper.Api.Endpoints;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Application.Tools;
using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Documents;
using Kasper.Infrastructure.Integrations;
using Kasper.Infrastructure.Mcp;
using Kasper.Infrastructure.Persistence;
using Kasper.Infrastructure.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PdfSharp.Pdf;

namespace Kasper.UnitTests;

public class SlackAndGitHubTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Calls.Add(r); return Task.FromResult(respond(r)); }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Slack_post_uses_bot_token_and_returns_ts()
    {
        var stub = new Stub(_ => Json("""{"ok":true,"ts":"123.456"}"""));
        var ts = await new SlackClient(new HttpClient(stub)).PostAsync("xoxb-test", "C1", "hello", null, CancellationToken.None);
        Assert.Equal("123.456", ts);
        Assert.Equal("Bearer", stub.Calls.Single().Headers.Authorization!.Scheme);
        Assert.EndsWith("chat.postMessage", stub.Calls.Single().RequestUri!.ToString());
    }

    [Theory]
    [InlineData("not_in_channel", "slack_not_in_channel", ErrorCategory.Authorization)]
    [InlineData("invalid_auth", "slack_unauthorized", ErrorCategory.Authorization)]
    [InlineData("ratelimited", "slack_rate_limited", ErrorCategory.RateLimit)]
    public async Task Slack_errors_inside_200_responses_are_classified(string slackError, string code, ErrorCategory category)
    {
        var client = new SlackClient(new HttpClient(new Stub(_ => Json($$"""{"ok":false,"error":"{{slackError}}"}"""))));
        var ex = await Assert.ThrowsAsync<ToolException>(() => client.PostAsync("xoxb-x", "C1", "hi", null, CancellationToken.None));
        Assert.Equal(code, ex.Code);
        Assert.Equal(category, ex.Category);
    }

    [Fact]
    public void Slack_channels_must_be_allowlisted()
    {
        var s = new SlackSettings("xoxb", "Team", "U1", [new SlackChannel("C1", "team")]);
        Assert.Equal("C1", SlackSettingsStore.Channel(s, "#Team").Id);
        Assert.Throws<ToolException>(() => SlackSettingsStore.Channel(s, "#random"));
    }

    [Fact]
    public async Task GitHub_missing_repo_is_explained()
    {
        var client = new GitHubClient(new HttpClient(new Stub(_ => Json("{}", HttpStatusCode.NotFound))), new GitHubSettingsStore());
        var ex = await Assert.ThrowsAsync<ToolException>(() => client.SummaryAsync("nope/nope", CancellationToken.None));
        Assert.Equal("github_not_found", ex.Code);
    }

    [Fact]
    public async Task GitHub_summary_lists_prs_issues_and_commits_as_untrusted_data()
    {
        var stub = new Stub(r => r.RequestUri!.AbsolutePath switch
        {
            "/repos/o/r" => Json("""{"html_url":"https://github.com/o/r","description":"demo","open_issues_count":2}"""),
            "/repos/o/r/pulls" => Json("""[{"number":5,"title":"Add login","user":{"login":"amy"},"html_url":"u"}]"""),
            "/repos/o/r/issues" => Json("""[{"number":6,"title":"Bug","html_url":"u"},{"number":5,"title":"PR","html_url":"u","pull_request":{}}]"""),
            _ => Json("""[{"sha":"abcdef1234","commit":{"message":"fix: thing\nbody","author":{"name":"Bo"}}}]"""),
        });
        var text = GitHubClient.Format(await new GitHubClient(new HttpClient(stub), new GitHubSettingsStore()).SummaryAsync("o/r", CancellationToken.None));
        Assert.Contains("#5 Add login by amy", text);
        Assert.Contains("#6 Bug", text);
        Assert.DoesNotContain("#5 PR", text);           // PRs are not double-listed as issues
        Assert.Contains("abcdef1 fix: thing — Bo", text);
        Assert.StartsWith("<<github", text);
    }
}

public class McpAndScheduleTests
{
    private static readonly McpServerOptions Server = new() { Name = "Files", AllowedTools = ["read", "write"], ReadOnlyTools = ["read"] };

    [Fact]
    public void Mcp_tools_need_approval_unless_marked_read_only()
    {
        var schema = JsonNode.Parse("""{"inputSchema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}""")!.AsObject();
        var read = McpHub.Describe(Server, "read", schema)!;
        var write = McpHub.Describe(Server, "write", schema)!;
        Assert.False(read.RequiresApproval);
        Assert.True(write.RequiresApproval);
        Assert.Equal("mcp.files.write", write.Id);
        Assert.Equal(ParamType.Text, write.Parameters.Single().Type);
    }

    [Fact]
    public void Mcp_tools_with_unsupported_required_inputs_are_skipped()
    {
        var schema = JsonNode.Parse("""{"inputSchema":{"properties":{"items":{"type":"array"}},"required":["items"]}}""")!.AsObject();
        Assert.Null(McpHub.Describe(Server, "write", schema));
    }

    [Theory]
    [InlineData("daily", DayOfWeek.Sunday, true)]
    [InlineData("weekdays", DayOfWeek.Saturday, false)]
    [InlineData("weekdays", DayOfWeek.Monday, true)]
    [InlineData("Mon,Thu", DayOfWeek.Thursday, true)]
    [InlineData("Mon,Thu", DayOfWeek.Friday, false)]
    public void Schedule_days(string days, DayOfWeek day, bool runs) =>
        Assert.Equal(runs, new Schedule { Name = "x", Request = "x", TimeOfDay = "09:00", Days = days }.RunsOn(day));
}

public class ArchitectureTests
{
    [Fact]
    public void Domain_depends_on_nothing_else_in_kasper()
    {
        var refs = typeof(Workflow).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(refs, r => r.StartsWith("Kasper.") || r.StartsWith("Microsoft.EntityFrameworkCore") || r.StartsWith("Microsoft.AspNetCore"));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_web()
    {
        var refs = typeof(Kasper.Application.KasperPaths).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(refs, r => r is "Kasper.Infrastructure" or "Kasper.Api" || r.StartsWith("Microsoft.EntityFrameworkCore") || r.StartsWith("Microsoft.AspNetCore"));
    }

    [Fact]
    public void There_is_no_shell_or_arbitrary_http_tool()
    {
        var toolTypes = typeof(Kasper.Infrastructure.DependencyInjection).Assembly.GetTypes()
            .Where(t => typeof(ITool).IsAssignableFrom(t) && !t.IsAbstract).Select(t => t.Name).ToList();
        Assert.DoesNotContain(toolTypes, n => n.Contains("Shell", StringComparison.OrdinalIgnoreCase) || n.Contains("Http", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>End-to-end through the API: projects, briefings, stamping and one-time passwords.</summary>
public sealed class ProjectAndPdfExtrasTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private KasperApiHost _host = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _host = KasperApiHost.Create("none", s => s.AddSingleton<IChatModel>(new CannedModel()));
        await _host.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.DisposeAsync();
    }

    private sealed class CannedModel : IChatModel
    {
        public Task<ChatResponseDto> ChatAsync(IReadOnlyList<ChatMessageDto> m, string? model, CancellationToken ct, ChatOptions? o = null) =>
            Task.FromResult(new ChatResponseDto("## Summary\nAll good.", "canned", 0));
    }

    [Fact]
    public async Task Project_links_a_folder_and_produces_a_sourced_briefing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kasper-test-project-" + Guid.NewGuid().ToString("N")); // outside KASPER data, which is never allowed
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "plan.md"), "x");

        var res = await _http.PostAsJsonAsync("/api/projects", new { name = "Demo", description = "d", folderPath = folder, notes = "remember X" }, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var p = (await res.Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        var folders = await _http.GetFromJsonAsync<List<FolderDto>>("/api/settings/folders", Json);
        Assert.Contains(folders!, f => f.Path == folder);                        // FR-306: folder is now allowed

        var briefed = (await (await _http.PostAsync($"/api/projects/{p.Id}/briefing", null)).Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        Assert.Contains("All good.", briefed.LastBriefing);
        Assert.Contains("folder " + folder, briefed.LastBriefing);              // sources listed

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/projects", new { name = "Bad", gitHubRepo = "not a repo" }, Json)).StatusCode);
    }

    private async Task<WorkflowDetailDto> RunAsync(DocumentJobDto job)
    {
        var res = await _http.PostAsJsonAsync("/api/documents/jobs", job, Json);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var wf = (await res.Content.ReadFromJsonAsync<WorkflowSummaryDto>(Json))!;
        for (var i = 0; i < 300; i++)
        {
            var d = (await _http.GetFromJsonAsync<WorkflowDetailDto>($"/api/workflows/{wf.Id}", Json))!;
            if (d.Workflow.Status is "Succeeded" or "Failed" or "Cancelled") return d;
            foreach (var a in d.Approvals.Where(a => a.Status == "Pending"))
                await _http.PostAsJsonAsync($"/api/approvals/{a.Id}/decision", new DecisionDto("approve"), Json);
            await Task.Delay(100);
        }
        throw new TimeoutException();
    }

    private async Task<Guid> UploadPdfAsync(int pages)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(ms.ToArray()), "files", $"in-{Guid.NewGuid():N}.pdf" } };
        var ups = await (await _http.PostAsync("/api/documents/upload", form)).Content.ReadFromJsonAsync<List<UploadDto>>(Json);
        return ups![0].Upload;
    }

    [Fact]
    public async Task Watermark_and_page_numbers()
    {
        var d = await RunAsync(new DocumentJobDto("stamp", [await UploadPdfAsync(2)], Watermark: "DRAFT", PageNumbers: true));
        Assert.Equal("Succeeded", d.Workflow.Status);
        Assert.Equal(2, PdfOps.PageCount(d.Artifacts.Single(a => a.CommittedPath is not null).CommittedPath!));
    }

    [Fact]
    public async Task Passwords_are_never_stored()
    {
        const string password = "s3cret-Pa55-for-test";
        var d = await RunAsync(new DocumentJobDto("protect", [await UploadPdfAsync(1)], Password: password));
        if (EngineLocator.Find("QPDF") is null) Assert.Equal("engine_missing", d.Workflow.FailureCode);
        else Assert.Equal("Succeeded", d.Workflow.Status);

        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KasperDbContext>();
        var inputs = await db.Steps.Where(s => s.WorkflowId == d.Workflow.Id).Select(s => s.InputJson).ToListAsync();
        Assert.DoesNotContain(inputs, i => i.Contains(password));
        Assert.Contains(inputs, i => i.Contains("secret:"));
    }

    [Fact]
    public async Task Planner_cannot_supply_passwords()
    {
        var plan = new PlanDto("x", [new PlanStepDto("s1", "pdf.protect", null, new JsonObject { ["artifact"] = "$s0.artifact", ["password"] = "guessed" })]);
        var res = await _http.PostAsJsonAsync("/api/workflows", new CreateWorkflowDto("x", plan), Json);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
