using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kasper.Application;
using Kasper.Application.Contracts;

namespace Kasper.Cli;

/// <summary>
/// Talks to the running KASPER desktop process over its loopback API (FR-503).
/// Starts KASPER in the background if it is not running (FR-504).
/// </summary>
internal sealed class KasperClient : IDisposable
{
    private readonly HttpClient _http;

    private KasperClient(SessionInfo session)
    {
        Session = session;
        _http = new HttpClient { BaseAddress = new Uri(session.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
    }

    public SessionInfo Session { get; }

    public static async Task<KasperClient> ConnectAsync(bool startIfNeeded, CancellationToken ct)
    {
        if (await TryExistingAsync(ct) is { } client) return client;
        if (!startIfNeeded) throw new KasperCliException("KASPER is not running. Run `kasper open` to start it.");

        var exe = FindDesktopExe()
            ?? throw new KasperCliException("Cannot find Kasper.Desktop.exe. Reinstall KASPER or set KASPER_DESKTOP to its path.");
        // Shell-execute so the app does not inherit this terminal's handles (otherwise
        // a piped/redirected `kasper` call would wait for the app to exit).
        Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400, ct);
            if (await TryExistingAsync(ct) is { } started) return started;
        }
        throw new KasperCliException("KASPER did not start within 30 seconds. Try opening it from the Start menu.");
    }

    private static async Task<KasperClient?> TryExistingAsync(CancellationToken ct)
    {
        if (ReadSession() is not { } session) return null;
        var client = new KasperClient(session);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var res = await client._http.GetAsync("/health", timeout.Token);
            if (res.IsSuccessStatusCode) return client;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        client.Dispose();
        return null;
    }

    private static SessionInfo? ReadSession()
    {
        try
        {
            return File.Exists(KasperPaths.SessionFile)
                ? JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(KasperPaths.SessionFile))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? FindDesktopExe()
    {
        var fromEnv = Environment.GetEnvironmentVariable("KASPER_DESKTOP");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var sideBySide = Path.Combine(AppContext.BaseDirectory, "Kasper.Desktop.exe");
        if (File.Exists(sideBySide)) return sideBySide;

        // Development: walk up to the repo and use the desktop build output.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Kasper.sln"))) continue;
            return new[] { "Debug", "Release" }
                .Select(c => Path.Combine(dir.FullName, "src", "Kasper.Desktop", "bin", c, "net10.0-windows", "Kasper.Desktop.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        return null;
    }

    public async Task<ReadyDto> ReadyAsync(CancellationToken ct) =>
        (await _http.GetFromJsonAsync<ReadyDto>("/ready", Json, ct))!;

    public async Task<HealthDto> HealthAsync(CancellationToken ct) =>
        (await _http.GetFromJsonAsync<HealthDto>("/health", Json, ct))!;

    public Task<AssistantResponseDto> AssistantAsync(IReadOnlyList<ChatMessageDto> messages, CancellationToken ct) =>
        SendAsync<AssistantResponseDto>(HttpMethod.Post, "/api/assistant", new AssistantRequestDto(messages), ct);

    public Task<IReadOnlyList<WorkflowSummaryDto>> WorkflowsAsync(int take, CancellationToken ct) =>
        SendAsync<IReadOnlyList<WorkflowSummaryDto>>(HttpMethod.Get, $"/api/workflows?take={take}", null, ct);

    public Task<WorkflowDetailDto> WorkflowAsync(Guid id, CancellationToken ct) =>
        SendAsync<WorkflowDetailDto>(HttpMethod.Get, $"/api/workflows/{id}", null, ct);

    public Task<IReadOnlyList<ApprovalDto>> ApprovalsAsync(CancellationToken ct) =>
        SendAsync<IReadOnlyList<ApprovalDto>>(HttpMethod.Get, "/api/approvals", null, ct);

    public Task DecideAsync(Guid approvalId, DecisionDto decision, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, $"/api/approvals/{approvalId}/decision", decision, ct);

    public Task CancelAsync(Guid workflowId, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, $"/api/workflows/{workflowId}/cancel", null, ct);

    public async Task<List<Guid>> UploadAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var streams = new List<FileStream>();
        try
        {
            foreach (var p in paths)
            {
                var fs = File.OpenRead(p);
                streams.Add(fs);
                form.Add(new StreamContent(fs), "files", Path.GetFileName(p));
            }
            using var res = await _http.PostAsync("/api/documents/upload", form, ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadFromJsonAsync<ErrorDto>(Json, ct);
                throw new KasperCliException(err?.Message ?? "Upload failed.");
            }
            var list = await res.Content.ReadFromJsonAsync<List<JsonElement>>(Json, ct);
            return list!.Select(e => e.GetProperty("upload").GetGuid()).ToList();
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }

    public Task<WorkflowSummaryDto> DocumentJobAsync(object job, CancellationToken ct) =>
        SendAsync<WorkflowSummaryDto>(HttpMethod.Post, "/api/documents/jobs", job, ct);

    public Task ShowWindowAsync(CancellationToken ct, string? route = null) =>
        SendAsync<object>(HttpMethod.Post, "/api/app/show" + (route is null ? "" : $"?route={route}"), null, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        using var res = await _http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode)
            return res.Content.Headers.ContentLength is 0 || res.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.Accepted
                ? default!
                : (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;

        ErrorDto? error = null;
        try { error = await res.Content.ReadFromJsonAsync<ErrorDto>(Json, ct); } catch (JsonException) { }
        var details = error?.Details is { Count: > 0 } d ? " " + string.Join(" ", d) : "";
        throw new KasperCliException((error?.Message ?? $"Request failed ({(int)res.StatusCode}).") + details);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Dispose() => _http.Dispose();
}

internal sealed class KasperCliException(string message) : Exception(message);
