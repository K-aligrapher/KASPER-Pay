using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kasper.Api;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Infrastructure.Files;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kasper.UnitTests;

/// <summary>
/// End-to-end runtime tests against the real local API, worker and SQLite database
/// (temp folder), with mock tools and a scripted model. No Ollama or accounts needed.
/// </summary>
public sealed class RuntimeTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ScriptedModel _model = new();
    private KasperApiHost _host = null!;
    private HttpClient _http = null!;

    public Task InitializeAsync() => StartHostAsync();

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.DisposeAsync();
    }

    private async Task StartHostAsync()
    {
        _host = KasperApiHost.Create("none", s => s.AddSingleton<IChatModel>(_model));
        await _host.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token);
    }

    /// <summary>Simulates closing and reopening KASPER: same database, new process state.</summary>
    private async Task RestartAsync()
    {
        _http.Dispose();
        await _host.DisposeAsync();
        await StartHostAsync();
    }

    // ---- helpers ------------------------------------------------------------------------

    private async Task<string> DefaultFolderRefAsync()
    {
        var folders = await _http.GetFromJsonAsync<List<FolderDto>>("/api/settings/folders", Json);
        return folders!.Single(f => f.IsDefault).Ref;
    }

    private async Task<WorkflowSummaryDto> CreateAsync(params PlanStepDto[] steps)
    {
        var res = await _http.PostAsJsonAsync("/api/workflows", new CreateWorkflowDto("test request", new PlanDto("Test workflow", steps)), Json);
        if (res.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"Create returned {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<WorkflowSummaryDto>(Json))!;
    }

    private static PlanStepDto Step(string id, string tool, params (string Key, JsonNode? Value)[] input) =>
        new(id, tool, null, new JsonObject(input.Select(p => KeyValuePair.Create(p.Key, p.Value))));

    private async Task<PlanStepDto[]> SaveNotePlanAsync(string fileName, string content) =>
    [
        Step("s1", "files.write_text", ("fileName", fileName), ("content", content)),
        Step("s2", "files.commit", ("artifact", "$s1.artifact"), ("folder", await DefaultFolderRefAsync())),
    ];

    private Task<WorkflowDetailDto> GetAsync(Guid id) => _http.GetFromJsonAsync<WorkflowDetailDto>($"/api/workflows/{id}", Json)!;

    private async Task<WorkflowDetailDto> WaitForAsync(Guid id, string status, Func<WorkflowDetailDto, bool>? extra = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        WorkflowDetailDto detail;
        do
        {
            detail = await GetAsync(id);
            if (detail.Workflow.Status == status && (extra is null || extra(detail))) return detail;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);
        Assert.Fail($"Workflow stayed {detail.Workflow.Status} ({detail.Workflow.FailureSummary}); expected {status}.");
        return detail;
    }

    private static Func<WorkflowDetailDto, bool> PendingApprovals(int n) => d => d.Approvals.Count(a => a.Status == "Pending") == n;

    private Task<HttpResponseMessage> DecideAsync(Guid approvalId, string decision, Dictionary<string, string>? edits = null) =>
        _http.PostAsJsonAsync($"/api/approvals/{approvalId}/decision", new DecisionDto(decision, edits), Json);

    private static string SavedPath(string name) => Path.Combine(TestEnvironment.SaveFolder, name);

    private async Task<T> WithDbAsync<T>(Func<KasperDbContext, Task<T>> f)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await f(scope.ServiceProvider.GetRequiredService<KasperDbContext>());
    }

    // ---- Final Review -------------------------------------------------------------------

    [Fact]
    public async Task Saving_a_file_waits_for_approval_then_saves_exactly_once()
    {
        var name = $"note-{Guid.NewGuid():N}.md";
        var wf = await CreateAsync(await SaveNotePlanAsync(name, "# Hello\nfrom KASPER"));

        var waiting = await WaitForAsync(wf.Id, "WaitingForApproval", PendingApprovals(1));
        Assert.False(File.Exists(SavedPath(name)));                         // nothing saved yet
        var approval = waiting.Approvals.Single();
        Assert.Equal($"Save {name}", approval.Title);
        Assert.Contains("from KASPER", approval.Content);                   // faithful preview
        Assert.Equal(SavedPath(name), approval.Target);

        var pending = await _http.GetFromJsonAsync<List<ApprovalDto>>("/api/approvals", Json);
        Assert.Contains(pending!, a => a.Id == approval.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await DecideAsync(approval.Id, "approve")).StatusCode);
        var done = await WaitForAsync(wf.Id, "Succeeded");

        Assert.Equal("# Hello\nfrom KASPER", await File.ReadAllTextAsync(SavedPath(name)));
        Assert.Equal(SavedPath(name), done.Artifacts.Single().CommittedPath);
        Assert.False(Directory.Exists(ArtifactStore.StagingDir(wf.Id)));     // staging cleaned
        Assert.Contains(done.Audit, a => a.EventType == "approval.approved");
        Assert.Contains(done.Audit, a => a.EventType == "approval.verified");

        // Deciding again is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(approval.Id, "approve")).StatusCode);
    }

    [Fact]
    public async Task Rejecting_saves_nothing_and_cleans_staging()
    {
        var name = $"rejected-{Guid.NewGuid():N}.md";
        var wf = await CreateAsync(await SaveNotePlanAsync(name, "nope"));
        var waiting = await WaitForAsync(wf.Id, "WaitingForApproval", PendingApprovals(1));

        await DecideAsync(waiting.Approvals.Single().Id, "reject");
        var done = await WaitForAsync(wf.Id, "Cancelled");

        Assert.False(File.Exists(SavedPath(name)));
        Assert.False(Directory.Exists(ArtifactStore.StagingDir(wf.Id)));
        Assert.Equal("Rejected in Final Review.", done.Workflow.FailureSummary);
    }

    [Fact]
    public async Task Editing_invalidates_the_old_approval_and_asks_again()
    {
        var name = $"draft-{Guid.NewGuid():N}.md";
        var renamed = $"final-{Guid.NewGuid():N}.md";
        var wf = await CreateAsync(await SaveNotePlanAsync(name, "text"));
        var first = (await WaitForAsync(wf.Id, "WaitingForApproval", PendingApprovals(1))).Approvals.Single();

        var edit = await DecideAsync(first.Id, "edit", new() { ["fileName"] = renamed });
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);

        var second = await WaitForAsync(wf.Id, "WaitingForApproval", d => d.Approvals.Any(a => a.Status == "Pending" && a.Id != first.Id));
        Assert.Equal("Invalidated", second.Approvals.Single(a => a.Id == first.Id).Status);
        var fresh = second.Approvals.Single(a => a.Status == "Pending");
        Assert.Equal(SavedPath(renamed), fresh.Target);

        await DecideAsync(fresh.Id, "approve");
        await WaitForAsync(wf.Id, "Succeeded");
        Assert.True(File.Exists(SavedPath(renamed)));
        Assert.False(File.Exists(SavedPath(name)));
    }

    [Theory]
    [InlineData("artifact", "$s1.artifact")] // not editable
    [InlineData("fileName", "..\\escape.md")] // unsafe
    [InlineData("folder", "folder:999")]      // not allowed
    public async Task Invalid_edits_are_rejected(string field, string value)
    {
        var wf = await CreateAsync(await SaveNotePlanAsync($"e-{Guid.NewGuid():N}.md", "x"));
        var approval = (await WaitForAsync(wf.Id, "WaitingForApproval", PendingApprovals(1))).Approvals.Single();
        var res = await DecideAsync(approval.Id, "edit", new() { [field] = value });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        await _http.PostAsync($"/api/workflows/{wf.Id}/cancel", null);
    }

    [Fact]
    public async Task File_changed_after_approval_is_caught_before_saving()
    {
        var name = $"tamper-{Guid.NewGuid():N}.md";
        var wf = await CreateAsync(await SaveNotePlanAsync(name, "original"));
        var waiting = await WaitForAsync(wf.Id, "WaitingForApproval", PendingApprovals(1));

        // Something modifies the staged file after the user saw the preview.
        var staged = Directory.GetFiles(ArtifactStore.StagingDir(wf.Id), name, SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(staged, "tampered");

        await DecideAsync(waiting.Approvals.Single().Id, "approve");
        var again = await WaitForAsync(wf.Id, "WaitingForApproval",
            d => d.Approvals.Any(a => a.Status == "Invalidated") && d.Approvals.Any(a => a.Status == "Pending"));

        Assert.False(File.Exists(SavedPath(name)));                              // never saved unseen content
        Assert.Contains("tampered", again.Approvals.Single(a => a.Status == "Pending").Content); // new preview shows the change
        await _http.PostAsync($"/api/workflows/{wf.Id}/cancel", null);
    }

    [Fact]
    public async Task Existing_files_are_never_overwritten()
    {
        var name = $"exists-{Guid.NewGuid():N}.md";
        Directory.CreateDirectory(TestEnvironment.SaveFolder);
        await File.WriteAllTextAsync(SavedPath(name), "keep me");

        var wf = await CreateAsync(await SaveNotePlanAsync(name, "new content"));
        var approval = (await WaitForAsync(wf.Id, "WaitingForApproval", PendingApprovals(1))).Approvals.Single();
        Assert.Contains(approval.Warnings, w => w.Contains("nothing is overwritten"));

        await DecideAsync(approval.Id, "approve");
        var done = await WaitForAsync(wf.Id, "Succeeded");

        Assert.Equal("keep me", await File.ReadAllTextAsync(SavedPath(name)));
        var copy = SavedPath($"{Path.GetFileNameWithoutExtension(name)} (2).md");
        Assert.Equal("new content", await File.ReadAllTextAsync(copy));
        Assert.Equal(copy, done.Artifacts.Single().CommittedPath);
    }

    [Fact]
    public async Task Several_actions_are_reviewed_together()
    {
        var wf = await CreateAsync(
            Step("s1", "mock.external_post", ("channel", "team"), ("text", "first")),
            Step("s2", "mock.echo", ("text", "between")),
            Step("s3", "mock.external_post", ("channel", "team"), ("text", "second")));

        var waiting = await WaitForAsync(wf.Id, "WaitingForApproval", PendingApprovals(2));
        foreach (var a in waiting.Approvals) await DecideAsync(a.Id, "approve");
        await WaitForAsync(wf.Id, "Succeeded");
    }

    // ---- reliability --------------------------------------------------------------------

    [Fact]
    public async Task Transient_failures_are_retried_within_limits()
    {
        var ok = await CreateAsync(Step("s1", "mock.flaky", ("failTimes", 2)));
        var done = await WaitForAsync(ok.Id, "Succeeded");
        Assert.Equal(3, done.Steps.Single().AttemptCount);
        Assert.Equal(2, done.Audit.Count(a => a.EventType == "step.retry"));

        var tooMany = await CreateAsync(Step("s1", "mock.flaky", ("failTimes", 10)));
        var failed = await WaitForAsync(tooMany.Id, "Failed");
        Assert.Equal(3, failed.Steps.Single().AttemptCount);           // bounded (MaxAttempts = 3)
        Assert.Equal("mock_transient", failed.Workflow.FailureCode);
    }

    [Fact]
    public async Task Permanent_failures_are_not_retried_and_can_be_retried_by_the_user()
    {
        var wf = await CreateAsync(Step("s1", "mock.flaky", ("failTimes", 0), ("permanent", "yes")));
        var failed = await WaitForAsync(wf.Id, "Failed");
        Assert.Equal(1, failed.Steps.Single().AttemptCount);

        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsync($"/api/workflows/{wf.Id}/retry", null)).StatusCode);
        var again = await WaitForAsync(wf.Id, "Failed", d => d.Steps.Single().AttemptCount == 2);
        Assert.Contains(again.Audit, a => a.EventType == "workflow.retry");
    }

    [Fact]
    public async Task Timeouts_are_recorded()
    {
        var wf = await CreateAsync(Step("s1", "mock.slow", ("ms", 5000)));   // tool timeout is 2s
        var failed = await WaitForAsync(wf.Id, "Failed");
        Assert.Equal("timeout", failed.Steps.Single().ErrorCode);
    }

    [Fact]
    public async Task Cancelling_stops_a_running_step()
    {
        var wf = await CreateAsync(Step("s1", "mock.slow", ("ms", 1500)), Step("s2", "mock.echo", ("text", "never")));
        await WaitForAsync(wf.Id, "Running");
        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsync($"/api/workflows/{wf.Id}/cancel", null)).StatusCode);

        var done = await WaitForAsync(wf.Id, "Cancelled");
        Assert.All(done.Steps, s => Assert.Equal("Cancelled", s.Status));
    }

    [Fact]
    public async Task Approval_waiting_survives_a_restart()
    {
        var name = $"restart-{Guid.NewGuid():N}.md";
        var wf = await CreateAsync(await SaveNotePlanAsync(name, "still here"));
        var approval = (await WaitForAsync(wf.Id, "WaitingForApproval", PendingApprovals(1))).Approvals.Single();

        await RestartAsync();

        var after = await GetAsync(wf.Id);
        Assert.Equal("WaitingForApproval", after.Workflow.Status);
        Assert.Equal("Pending", after.Approvals.Single().Status);

        await DecideAsync(approval.Id, "approve");
        await WaitForAsync(wf.Id, "Succeeded");
        Assert.Equal("still here", await File.ReadAllTextAsync(SavedPath(name)));
    }

    [Fact]
    public async Task External_write_interrupted_by_a_crash_is_replayed_without_duplicates()
    {
        var channel = $"c{Guid.NewGuid():N}"[..20];
        var wf = await CreateAsync(Step("s1", "mock.external_post", ("channel", channel), ("text", "ship it")));
        var approval = (await WaitForAsync(wf.Id, "WaitingForApproval", PendingApprovals(1))).Approvals.Single();
        await DecideAsync(approval.Id, "approve");
        await WaitForAsync(wf.Id, "Succeeded");

        // Simulate a crash right after the external write: the step is still marked Running.
        await WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Steps SET Status = 'Running' WHERE WorkflowId = {wf.Id}; UPDATE Workflows SET Status = 'Running' WHERE Id = {wf.Id};"));
        await RestartAsync();

        var done = await WaitForAsync(wf.Id, "Succeeded", d => d.Audit.Any(a => a.EventType == "step.recovered"));
        Assert.Equal(1, await WithDbAsync(db => db.MockOutbox.CountAsync(o => o.Channel == channel)));
        Assert.Equal("Succeeded", done.Steps.Single().Status);
    }

    // ---- validation & assistant ---------------------------------------------------------

    [Fact]
    public async Task Invalid_plans_are_rejected_with_reasons()
    {
        var res = await _http.PostAsJsonAsync("/api/workflows",
            new CreateWorkflowDto("x", new PlanDto("Bad", [Step("s1", "shell.execute", ("cmd", "format c:"))])), Json);
        var error = await res.Content.ReadFromJsonAsync<ErrorDto>(Json);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(error!.Details!, d => d.Contains("unknown tool"));
    }

    [Fact]
    public async Task Assistant_answers_chat_without_starting_work()
    {
        _model.Next("""{"reply":"Hi! I'm KASPER.","summary":"","steps":[]}""");
        var res = await _http.PostAsJsonAsync("/api/assistant", new AssistantRequestDto([new("user", "hello")]), Json);
        var body = await res.Content.ReadFromJsonAsync<AssistantResponseDto>(Json);
        Assert.Equal("reply", body!.Kind);
        Assert.Equal("Hi! I'm KASPER.", body.Reply);
    }

    [Fact]
    public async Task Assistant_repairs_an_invalid_plan_once_then_starts_the_workflow()
    {
        var folder = await DefaultFolderRefAsync();
        _model.Next("""{"reply":"ok","summary":"Save","steps":[{"id":"s1","tool":"files.commit","input":{"path":"C:\\x"}}]}""");
        _model.Next($$$"""
            {"reply":"I'll save it after you approve.","summary":"Save a test note","steps":[
              {"id":"s1","tool":"files.write_text","purpose":"prepare","input":{"fileName":"assistant-{{{Guid.NewGuid():N}}}.md","content":"hi"}},
              {"id":"s2","tool":"files.commit","purpose":"save","input":{"artifact":"$s1.artifact","folder":"{{{folder}}}"}}]}
            """);

        var res = await _http.PostAsJsonAsync("/api/assistant", new AssistantRequestDto([new("user", "save a note")]), Json);
        var body = await res.Content.ReadFromJsonAsync<AssistantResponseDto>(Json);

        Assert.Equal("workflow", body!.Kind);
        Assert.Equal("Save a test note", body.Workflow!.Summary);
        Assert.Contains("invalid", _model.Seen.Last().Last().Content);   // repair round sent the errors back
        await WaitForAsync(body.Workflow.Id, "WaitingForApproval", PendingApprovals(1));
        await _http.PostAsync($"/api/workflows/{body.Workflow.Id}/cancel", null);
    }

    [Fact]
    public async Task Approving_requires_the_session_token()
    {
        var wf = await CreateAsync(await SaveNotePlanAsync($"t-{Guid.NewGuid():N}.md", "x"));
        var approval = (await WaitForAsync(wf.Id, "WaitingForApproval", PendingApprovals(1))).Approvals.Single();

        using var anonymous = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };
        var res = await anonymous.PostAsJsonAsync($"/api/approvals/{approval.Id}/decision", new DecisionDto("approve"), Json);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("Pending", (await GetAsync(wf.Id)).Approvals.Single().Status);
        await _http.PostAsync($"/api/workflows/{wf.Id}/cancel", null);
    }

    [Fact]
    public async Task Folders_cannot_be_system_locations()
    {
        var res = await _http.PostAsJsonAsync("/api/settings/folders", new AddFolderDto(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), Json);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var drive = await _http.PostAsJsonAsync("/api/settings/folders", new AddFolderDto("C:\\"), Json);
        Assert.Equal(HttpStatusCode.BadRequest, drive.StatusCode);
    }

    /// <summary>Returns queued responses in order; records what it was sent.</summary>
    private sealed class ScriptedModel : IChatModel
    {
        private readonly Queue<string> _responses = new();
        public List<IReadOnlyList<ChatMessageDto>> Seen { get; } = [];

        public void Next(string response) => _responses.Enqueue(response);

        public Task<ChatResponseDto> ChatAsync(IReadOnlyList<ChatMessageDto> messages, string? model, CancellationToken ct, ChatOptions? options = null)
        {
            Seen.Add(messages.ToList());
            return Task.FromResult(new ChatResponseDto(_responses.Count > 0 ? _responses.Dequeue() : "generated text", "scripted", 0));
        }
    }
}
