using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kasper.Api;
using Kasper.Api.Endpoints;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Application.Tools;
using Kasper.Infrastructure.Documents;
using Kasper.Infrastructure.Integrations;
using Kasper.Infrastructure.Notion;
using Kasper.Infrastructure.Runtime;
using PdfSharp.Pdf;

namespace Kasper.UnitTests;

/// <summary>Notion page edits and Slack threads/uploads, against stubbed APIs.</summary>
public class NotionAndSlackFollowUpTests
{
    private sealed class Stub(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Calls.Add((r.Method, r.RequestUri!.ToString(), body));
            return respond(r, body);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly NotionSettings Settings = new("secret_x", "db1", "Tasks", "Name");

    private const string Page = """
        {"object":"page","id":"p1","url":"https://notion.so/p1",
         "properties":{"Name":{"type":"title","title":[{"plain_text":"Write report"}]},
                       "Status":{"type":"status","status":{"name":"In progress"}}}}
        """;

    [Fact]
    public async Task Notion_task_is_found_by_title_and_status_is_changed()
    {
        var stub = new Stub((r, _) => Json(r.RequestUri!.AbsolutePath.EndsWith("/query") ? $$"""{"results":[{{Page}}]}""" : Page));
        var notion = new NotionClient(new HttpClient(stub));
        var page = await notion.ResolvePageAsync(Settings, "Write report", CancellationToken.None);
        Assert.Equal(("Status", "status", "In progress"), NotionClient.StatusProperty(page));

        await notion.UpdatePageAsync(Settings, page, "Done", null, CancellationToken.None);
        var patch = stub.Calls.Single(c => c.Method == HttpMethod.Patch);
        Assert.EndsWith("pages/p1", patch.Url);
        Assert.Equal("Done", JsonNode.Parse(patch.Body)!["properties"]!["Status"]!["status"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notion_checkbox_tasks_are_ticked()
    {
        var page = JsonNode.Parse("""{"id":"p2","properties":{"Done":{"type":"checkbox","checkbox":false}}}""")!.AsObject();
        var stub = new Stub((_, _) => Json("{}"));
        await new NotionClient(new HttpClient(stub)).UpdatePageAsync(Settings, page, "done", null, CancellationToken.None);
        Assert.True(JsonNode.Parse(stub.Calls.Single().Body)!["properties"]!["Done"]!["checkbox"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Unknown_notion_task_is_explained()
    {
        var notion = new NotionClient(new HttpClient(new Stub((_, _) => Json("""{"results":[]}"""))));
        var ex = await Assert.ThrowsAsync<ToolException>(() => notion.ResolvePageAsync(Settings, "Nope", CancellationToken.None));
        Assert.Equal("notion_page_not_found", ex.Code);
    }

    [Fact]
    public async Task Notion_append_sends_one_block_per_paragraph()
    {
        var stub = new Stub((_, _) => Json("{}"));
        await new NotionClient(new HttpClient(stub)).AppendAsync("t", "p1", "First.\r\n\r\nSecond.", CancellationToken.None);
        var call = stub.Calls.Single();
        Assert.EndsWith("blocks/p1/children", call.Url);
        Assert.Equal(2, JsonNode.Parse(call.Body)!["children"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("1712345678.123456", "1712345678.123456")]
    [InlineData("[1712345678.123456]", "1712345678.123456")]
    [InlineData("p1712345678123456", "1712345678.123456")] // from a Slack message link
    public void Slack_thread_ids_are_normalised(string input, string expected) => Assert.Equal(expected, SlackClient.ThreadTs(input));

    [Fact]
    public void Bad_thread_id_is_rejected() =>
        Assert.Equal("thread_invalid", Assert.Throws<ToolException>(() => SlackClient.ThreadTs("yesterday")).Code);

    [Fact]
    public async Task Slack_reply_goes_to_the_thread()
    {
        var stub = new Stub((_, _) => Json("""{"ok":true,"ts":"2.2"}"""));
        await new SlackClient(new HttpClient(stub)).PostAsync("xoxb", "C1", "hi", "1712345678.123456", CancellationToken.None);
        Assert.Equal("1712345678.123456", JsonNode.Parse(stub.Calls.Single().Body)!["thread_ts"]!.GetValue<string>());
    }

    [Fact]
    public async Task Slack_upload_uses_external_upload_flow()
    {
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, "report");
        var stub = new Stub((r, _) => r.RequestUri!.Host == "files.example"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : Json("""{"ok":true,"upload_url":"https://files.example/u","file_id":"F1"}"""));
        await new SlackClient(new HttpClient(stub)).UploadAsync("xoxb", "C1", file, "report.pdf", "Here it is", "1712345678.123456", CancellationToken.None);

        Assert.Contains("files.getUploadURLExternal?filename=report.pdf&length=6", stub.Calls[0].Url);
        Assert.Equal("https://files.example/u", stub.Calls[1].Url);
        var complete = JsonNode.Parse(stub.Calls[2].Body)!;
        Assert.Equal("F1", complete["files"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("C1", complete["channel_id"]!.GetValue<string>());
        Assert.Equal("1712345678.123456", complete["thread_ts"]!.GetValue<string>());
    }
}

/// <summary>Project tasks, PDF → images / repair / metadata, and Final Review file previews, through the API.</summary>
public sealed class ProjectTaskAndPdfToolTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private KasperApiHost _host = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _host = KasperApiHost.Create("none");
        await _host.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Project_tasks_can_be_added_ticked_and_deleted()
    {
        var p = (await (await _http.PostAsJsonAsync("/api/projects", new { name = "Tasks " + Guid.NewGuid().ToString("N")[..6] }, Json)).Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        var t = (await (await _http.PostAsJsonAsync($"/api/projects/{p.Id}/tasks", new { title = "Ship it" }, Json)).Content.ReadFromJsonAsync<ProjectTaskDto>(Json))!;
        Assert.False(t.Done);

        var done = (await (await _http.PutAsJsonAsync($"/api/projects/{p.Id}/tasks/{t.Id}", new { done = true }, Json)).Content.ReadFromJsonAsync<ProjectTaskDto>(Json))!;
        Assert.True(done.Done);
        Assert.NotNull(done.DoneAtUtc);

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync($"/api/projects/{p.Id}/tasks", new { title = " " }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/projects/{p.Id}/tasks/{t.Id}")).StatusCode);
        Assert.Empty((await _http.GetFromJsonAsync<List<ProjectTaskDto>>($"/api/projects/{p.Id}/tasks", Json))!);
    }

    [Fact]
    public async Task Adding_a_task_from_chat_waits_for_approval()
    {
        var name = "Chat " + Guid.NewGuid().ToString("N")[..6];
        var p = (await (await _http.PostAsJsonAsync("/api/projects", new { name }, Json)).Content.ReadFromJsonAsync<ProjectDto>(Json))!;
        var plan = new PlanDto("x", [new PlanStepDto("s1", "project.add_task", null, new JsonObject { ["project"] = name, ["title"] = "Review PR" })]);
        var wf = (await (await _http.PostAsJsonAsync("/api/workflows", new CreateWorkflowDto("add task", plan), Json)).Content.ReadFromJsonAsync<WorkflowSummaryDto>(Json))!;
        var d = await WaitAsync(wf.Id, approve: false);
        Assert.Equal("WaitingForApproval", d.Workflow.Status);
        Assert.Empty((await _http.GetFromJsonAsync<List<ProjectTaskDto>>($"/api/projects/{p.Id}/tasks", Json))!);

        d = await WaitAsync(wf.Id, approve: true);
        Assert.Equal("Succeeded", d.Workflow.Status);
        Assert.Single((await _http.GetFromJsonAsync<List<ProjectTaskDto>>($"/api/projects/{p.Id}/tasks", Json))!, t => t.Title == "Review PR");
    }

    private async Task<WorkflowDetailDto> WaitAsync(Guid id, bool approve)
    {
        for (var i = 0; i < 300; i++)
        {
            var d = (await _http.GetFromJsonAsync<WorkflowDetailDto>($"/api/workflows/{id}", Json))!;
            if (d.Workflow.Status is "Succeeded" or "Failed" or "Cancelled") return d;
            if (d.Workflow.Status == "WaitingForApproval")
            {
                if (!approve) return d;
                foreach (var a in d.Approvals.Where(a => a.Status == "Pending"))
                    await _http.PostAsJsonAsync($"/api/approvals/{a.Id}/decision", new DecisionDto("approve"), Json);
            }
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
        return (await (await _http.PostAsync("/api/documents/upload", form)).Content.ReadFromJsonAsync<List<UploadDto>>(Json))![0].Upload;
    }

    private async Task<WorkflowDetailDto> JobAsync(DocumentJobDto job)
    {
        var wf = (await (await _http.PostAsJsonAsync("/api/documents/jobs", job, Json)).Content.ReadFromJsonAsync<WorkflowSummaryDto>(Json))!;
        return await WaitAsync(wf.Id, approve: false);
    }

    [Fact]
    public async Task Final_review_can_show_the_pdf_itself()
    {
        var d = await JobAsync(new DocumentJobDto("metadata", [await UploadPdfAsync(1)], Title: "Q3 report", Author: "Me"));
        Assert.Equal("WaitingForApproval", d.Workflow.Status);
        var approval = d.Approvals.Single(a => a.Status == "Pending");
        Assert.NotNull(approval.PreviewArtifactId);

        var res = await _http.GetAsync($"/api/artifacts/{approval.PreviewArtifactId}/preview");
        Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        await File.WriteAllBytesAsync(path, await res.Content.ReadAsByteArrayAsync());
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal("Q3 report", doc.Info.Title);
        Assert.Equal("Me", doc.Info.Author);
    }

    [Fact]
    public async Task Pdf_pages_become_a_zip_of_images()
    {
        var d = await JobAsync(new DocumentJobDto("to_images", [await UploadPdfAsync(3)], Format: "png", Dpi: 72));
        if (EngineLocator.Find("Ghostscript") is null) { Assert.Equal("engine_missing", d.Workflow.FailureCode); return; }
        var zip = d.Artifacts.Single(a => a.FileName.EndsWith(".zip"));
        Assert.EndsWith("-pages.zip", zip.FileName);
    }

    [Fact]
    public async Task Damaged_pdf_is_repaired()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();
        var text = Encoding.Latin1.GetString(bytes);
        var broken = Encoding.Latin1.GetBytes(text[..text.LastIndexOf("xref", StringComparison.Ordinal)] + "%%EOF"); // cross-reference table cut off
        using var form = new MultipartFormDataContent { { new ByteArrayContent(broken), "files", "broken.pdf" } };
        var up = (await (await _http.PostAsync("/api/documents/upload", form)).Content.ReadFromJsonAsync<List<UploadDto>>(Json))![0].Upload;

        var d = await JobAsync(new DocumentJobDto("repair", [up]));
        if (EngineLocator.Find("QPDF") is null) { Assert.Equal("engine_missing", d.Workflow.FailureCode); return; }
        Assert.Equal("WaitingForApproval", d.Workflow.Status);
        Assert.Contains(d.Artifacts, a => a.FileName == "broken-repaired.pdf");
    }
}

public class OpenFileToolTests
{
    [Theory]
    [InlineData("EWaste_Seminar____20(1).docx", "ewaste seminar 20 (1).docx")]
    [InlineData("Q3 Report.PDF", "q3-report.pdf")]
    public void File_names_match_loosely(string onDisk, string typed) =>
        Assert.Equal(Kasper.Infrastructure.Tools.OpenFileTool.Loose(onDisk), Kasper.Infrastructure.Tools.OpenFileTool.Loose(typed));
}
