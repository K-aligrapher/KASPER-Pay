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
using Kasper.Application.Options;
using Kasper.Application.Tools;
using Kasper.Infrastructure.Documents;
using Kasper.Infrastructure.Notion;
using Kasper.Infrastructure.Tools;
using Microsoft.Extensions.Options;
using PdfSharp.Pdf;

namespace Kasper.UnitTests;

public sealed class DocumentStudioTests : IAsyncLifetime
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

    private static byte[] MakePdf(int pages)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private async Task<List<UploadDto>> UploadAsync(params (string Name, byte[] Bytes)[] files)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files) form.Add(new ByteArrayContent(bytes), "files", name);
        var res = await _http.PostAsync("/api/documents/upload", form);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<List<UploadDto>>(Json))!;
    }

    private async Task<WorkflowDetailDto> RunJobAsync(DocumentJobDto job, bool approve = true)
    {
        var res = await _http.PostAsJsonAsync("/api/documents/jobs", job, Json);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var wf = (await res.Content.ReadFromJsonAsync<WorkflowSummaryDto>(Json))!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var d = (await _http.GetFromJsonAsync<WorkflowDetailDto>($"/api/workflows/{wf.Id}", Json))!;
            if (d.Workflow.Status is "Succeeded" or "Failed" or "Cancelled") return d;
            if (d.Workflow.Status == "WaitingForApproval" && approve)
                foreach (var a in d.Approvals.Where(a => a.Status == "Pending"))
                    await _http.PostAsJsonAsync($"/api/approvals/{a.Id}/decision", new DecisionDto("approve"), Json);
            await Task.Delay(100);
        }
        throw new TimeoutException("Job did not finish.");
    }

    private static int Pages(string path) => PdfOps.PageCount(path);

    [Fact]
    public async Task Merge_waits_for_approval_then_saves_one_pdf_with_all_pages()
    {
        var up = await UploadAsync(($"a-{Guid.NewGuid():N}.pdf", MakePdf(2)), ("b.pdf", MakePdf(3)));
        var name = $"merged-{Guid.NewGuid():N}.pdf";
        var res = await _http.PostAsJsonAsync("/api/documents/jobs", new DocumentJobDto("merge", up.Select(u => u.Upload).ToList(), FileName: name), Json);
        var wf = (await res.Content.ReadFromJsonAsync<WorkflowSummaryDto>(Json))!;

        // Nothing is saved before approval.
        await Task.Delay(1500);
        Assert.False(File.Exists(Path.Combine(TestEnvironment.SaveFolder, name)));

        var done = await RunJobAsync(new DocumentJobDto("merge", (await UploadAsync(("x.pdf", MakePdf(2)), ("y.pdf", MakePdf(3)))).Select(u => u.Upload).ToList(), FileName: name));
        Assert.Equal("Succeeded", done.Workflow.Status);
        Assert.Equal(5, Pages(done.Artifacts.Single(a => a.CommittedPath is not null).CommittedPath!));
        await _http.PostAsync($"/api/workflows/{wf.Id}/cancel", null);
    }

    [Fact]
    public async Task Extract_rotate_and_compress_work_offline()
    {
        var extract = await RunJobAsync(new DocumentJobDto("extract", [(await UploadAsync(("e.pdf", MakePdf(5))))[0].Upload], Pages: "2-3,5"));
        Assert.Equal("Succeeded", extract.Workflow.Status);
        Assert.Equal(3, Pages(extract.Artifacts.Single(a => a.CommittedPath is not null).CommittedPath!));

        var rotate = await RunJobAsync(new DocumentJobDto("rotate", [(await UploadAsync(("r.pdf", MakePdf(2))))[0].Upload], Degrees: 90));
        Assert.Equal("Succeeded", rotate.Workflow.Status);

        var compress = await RunJobAsync(new DocumentJobDto("compress", [(await UploadAsync(("c.pdf", MakePdf(2))))[0].Upload]));
        Assert.Equal("Succeeded", compress.Workflow.Status);
    }

    [Fact]
    public async Task Bad_page_range_fails_clearly()
    {
        var d = await RunJobAsync(new DocumentJobDto("extract", [(await UploadAsync(("e.pdf", MakePdf(2))))[0].Upload], Pages: "5-9"));
        Assert.Equal("Failed", d.Workflow.Status);
        Assert.Equal("range_invalid", d.Workflow.FailureCode);
    }

    [Fact]
    public async Task Conversion_without_the_engine_explains_how_to_install_it()
    {
        var d = await RunJobAsync(new DocumentJobDto("convert", [(await UploadAsync(("notes.md", Encoding.UTF8.GetBytes("# Hi"))))[0].Upload], To: "docx"));
        if (EngineLocator.Find("Pandoc") is null)
        {
            Assert.Equal("Failed", d.Workflow.FailureCode is null ? "" : "Failed");
            Assert.Contains("winget install", d.Workflow.FailureSummary);
        }
        else
        {
            Assert.Equal("Succeeded", d.Workflow.Status);
        }
    }

    [Fact]
    public async Task Executables_cannot_be_uploaded()
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2]), "files", "evil.exe" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsync("/api/documents/upload", form)).StatusCode);
    }

    [Theory]
    [InlineData("1-3", 5, new[] { 1, 2, 3 })]
    [InlineData("5,1", 5, new[] { 5, 1 })]
    [InlineData("4-", 5, new[] { 4, 5 })]
    public void Page_ranges_parse(string range, int count, int[] expected) =>
        Assert.Equal(expected, PdfOps.ParseRange(range, count));
}

public class NotionTests
{
    private sealed class Stub(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly NotionSettings Settings = new("secret_token_value_123456", "abc", "Tasks", "Name");

    [Fact]
    public async Task Create_task_sends_title_notes_and_headers()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, """{"id":"p1","url":"https://notion.so/p1"}"""));
        var page = await new NotionClient(new HttpClient(stub)).CreateTaskAsync(Settings, "Ship M4", "First line\n\nSecond", CancellationToken.None);

        Assert.Equal("https://notion.so/p1", page.Url);
        var (req, body) = stub.Calls.Single();
        Assert.Equal("https://api.notion.com/v1/pages", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("2022-06-28", req.Headers.GetValues("Notion-Version").Single());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("abc", json["parent"]!["database_id"]!.GetValue<string>());
        Assert.Equal("Ship M4", json["properties"]!["Name"]!["title"]![0]!["text"]!["content"]!.GetValue<string>());
        Assert.Equal(2, json["children"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "notion_not_shared")]
    [InlineData(HttpStatusCode.Unauthorized, "notion_unauthorized")]
    [InlineData(HttpStatusCode.TooManyRequests, "notion_rate_limited")]
    [InlineData(HttpStatusCode.BadGateway, "notion_error")]
    public async Task Errors_are_classified(HttpStatusCode status, string code)
    {
        var client = new NotionClient(new HttpClient(new Stub((_, _) => Json(status, """{"message":"x"}"""))));
        var ex = await Assert.ThrowsAsync<ToolException>(() => client.CreateTaskAsync(Settings, "t", null, CancellationToken.None));
        Assert.Equal(code, ex.Code);
    }

    [Theory]
    [InlineData("https://www.notion.so/myteam/1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d?v=99999999999999999999999999999999", "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d")]
    [InlineData("1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d", "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d")]
    [InlineData("not a link", null)]
    public void Database_links_are_parsed(string input, string? expected) =>
        Assert.Equal(expected, NotionSettingsStore.ParseId(input));

    [Fact]
    public void Notion_tools_only_appear_when_connected()
    {
        ITool[] tools = [new NotionSearchTool(null!, null!), new GenerateTextTool(null!)];
        var off = new ToolRegistry(tools, new Opts(), new Status(false));
        var on = new ToolRegistry(tools, new Opts(), new Status(true));
        Assert.DoesNotContain(off.Available, t => t.Id == "notion.search");
        Assert.Contains(on.Available, t => t.Id == "notion.search");
    }

    private sealed class Status(bool connected) : IIntegrationStatus
    {
        public bool IsConnected(string integration) => connected;
    }

    private sealed class Opts : IOptionsMonitor<RuntimeOptions>
    {
        public RuntimeOptions CurrentValue { get; } = new();
        public RuntimeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<RuntimeOptions, string?> listener) => null;
    }
}
