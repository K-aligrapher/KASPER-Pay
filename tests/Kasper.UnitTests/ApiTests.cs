using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kasper.Api;
using Kasper.Application;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Kasper.UnitTests;

/// <summary>Runs the real local API (loopback, temp data folder) with a fake model.</summary>
public sealed class ApiTests : IAsyncLifetime
{
    private readonly FakeChatModel _model = new();
    private KasperApiHost _host = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _host = KasperApiHost.Create(webRoot: "does-not-exist", services => services.AddSingleton<IChatModel>(_model));
        await _host.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.DisposeAsync();
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    [Fact]
    public void Uses_temp_data_folder_in_tests() => Assert.StartsWith(TestEnvironment.Home, KasperPaths.Database);

    [Fact]
    public async Task Health_is_public()
    {
        var health = await _http.GetFromJsonAsync<HealthDto>("/health");
        Assert.Equal("ok", health!.Status);
    }

    [Fact]
    public async Task Api_requires_token()
    {
        var res = await _http.PostAsJsonAsync("/api/ai/chat", new ChatRequestDto([new("user", "hi")]));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(0, _model.Calls);
    }

    [Fact]
    public async Task Wrong_token_is_rejected()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/chat") { Content = JsonContent.Create(new ChatRequestDto([new("user", "hi")])) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "guess");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Non_loopback_host_header_is_rejected()
    {
        var req = Authorized(HttpMethod.Get, "/health");
        req.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await _http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Chat_adds_our_system_prompt_and_returns_reply()
    {
        var res = await _http.SendAsync(Authorized(HttpMethod.Post, "/api/ai/chat", new ChatRequestDto([new("user", "Reply with READY")])));
        res.EnsureSuccessStatusCode();
        var reply = await res.Content.ReadFromJsonAsync<ChatResponseDto>();

        Assert.Equal("READY", reply!.Content);
        Assert.Equal("system", _model.LastMessages![0].Role);
        Assert.Equal(Prompts.System, _model.LastMessages[0].Content);
    }

    [Theory]
    [InlineData("system")]   // callers cannot inject their own system prompt
    [InlineData("tool")]
    public async Task Chat_rejects_unexpected_roles(string role)
    {
        var res = await _http.SendAsync(Authorized(HttpMethod.Post, "/api/ai/chat", new ChatRequestDto([new(role, "x")])));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Chat_rejects_empty_and_oversized_requests()
    {
        var empty = await _http.SendAsync(Authorized(HttpMethod.Post, "/api/ai/chat", new ChatRequestDto([])));
        var huge = await _http.SendAsync(Authorized(HttpMethod.Post, "/api/ai/chat", new ChatRequestDto([new("user", new string('a', 20_000))])));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
    }

    [Fact]
    public async Task Model_errors_become_clear_502s()
    {
        _model.FailWith = new ChatModelException("model_unreachable", "Cannot reach Ollama.", retryable: true);
        var res = await _http.SendAsync(Authorized(HttpMethod.Post, "/api/ai/chat", new ChatRequestDto([new("user", "hi")])));
        var error = await res.Content.ReadFromJsonAsync<ErrorDto>();

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Equal("model_unreachable", error!.Code);
    }

    private sealed class FakeChatModel : IChatModel
    {
        public int Calls;
        public IReadOnlyList<ChatMessageDto>? LastMessages;
        public ChatModelException? FailWith;

        public Task<ChatResponseDto> ChatAsync(IReadOnlyList<ChatMessageDto> messages, string? model, CancellationToken ct)
        {
            Calls++;
            LastMessages = messages;
            if (FailWith is not null) throw FailWith;
            return Task.FromResult(new ChatResponseDto("READY", "fake", 0.01));
        }
    }
}
