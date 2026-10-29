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

    public async Task<ChatResponseDto> ChatAsync(IReadOnlyList<ChatMessageDto> messages, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("/api/ai/chat", new ChatRequestDto(messages), Json, ct);
        if (res.IsSuccessStatusCode) return (await res.Content.ReadFromJsonAsync<ChatResponseDto>(Json, ct))!;

        var error = await res.Content.ReadFromJsonAsync<ErrorDto>(Json, ct).ConfigureAwait(false);
        throw new KasperCliException(error?.Message ?? $"Request failed ({(int)res.StatusCode}).");
    }

    public async Task ShowWindowAsync(CancellationToken ct)
    {
        using var res = await _http.PostAsync("/api/app/show", null, ct);
        res.EnsureSuccessStatusCode();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Dispose() => _http.Dispose();
}

internal sealed class KasperCliException(string message) : Exception(message);
