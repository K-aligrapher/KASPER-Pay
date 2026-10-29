using System.Net;
using System.Text;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Application.Options;
using Kasper.Infrastructure.Ollama;
using Kasper.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Kasper.UnitTests;

public class OllamaChatModelTests
{
    private static OllamaChatModel Create(HttpStatusCode status, string body, Action<string>? seeRequest = null)
    {
        var handler = new StubHandler(async req =>
        {
            seeRequest?.Invoke(await req.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        var opts = new OllamaOptions { Model = "test-model", TimeoutSeconds = 5 };
        return new OllamaChatModel(new HttpClient(handler), new StaticOptions(opts));
    }

    private static readonly ChatMessageDto[] Hello = [new("user", "hello")];

    [Fact]
    public async Task Returns_model_reply_and_sends_expected_request()
    {
        string? sent = null;
        var model = Create(HttpStatusCode.OK, """{"model":"test-model","message":{"role":"assistant","content":" READY \n"}}""", r => sent = r);

        var reply = await model.ChatAsync(Hello, null, CancellationToken.None);

        Assert.Equal("READY", reply.Content);
        Assert.Contains("\"model\":\"test-model\"", sent);
        Assert.Contains("\"stream\":false", sent);
        Assert.Contains("\"keep_alive\":\"30m\"", sent);
    }

    [Fact]
    public async Task Missing_model_is_a_non_retryable_error_with_install_hint()
    {
        var model = Create(HttpStatusCode.NotFound, """{"error":"model \"test-model\" not found, try pulling it first"}""");
        var ex = await Assert.ThrowsAsync<ChatModelException>(() => model.ChatAsync(Hello, null, CancellationToken.None));
        Assert.Equal("model_not_installed", ex.Code);
        Assert.False(ex.Retryable);
        Assert.Contains("ollama pull test-model", ex.Message);
    }

    [Fact]
    public async Task Malformed_json_is_classified()
    {
        var model = Create(HttpStatusCode.OK, "{not json");
        var ex = await Assert.ThrowsAsync<ChatModelException>(() => model.ChatAsync(Hello, null, CancellationToken.None));
        Assert.Equal("model_bad_response", ex.Code);
    }

    [Fact]
    public async Task Server_errors_are_retryable()
    {
        var model = Create(HttpStatusCode.ServiceUnavailable, "{}");
        var ex = await Assert.ThrowsAsync<ChatModelException>(() => model.ChatAsync(Hello, null, CancellationToken.None));
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task Unreachable_ollama_is_reported_clearly()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("refused"));
        var model = new OllamaChatModel(new HttpClient(handler), new StaticOptions(new OllamaOptions()));
        var ex = await Assert.ThrowsAsync<ChatModelException>(() => model.ChatAsync(Hello, null, CancellationToken.None));
        Assert.Equal("model_unreachable", ex.Code);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    private sealed class StaticOptions(OllamaOptions value) : IOptionsMonitor<OllamaOptions>
    {
        public OllamaOptions CurrentValue => value;
        public OllamaOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<OllamaOptions, string?> listener) => null;
    }
}

public class SecurityTests
{
    [Fact]
    public void Tokens_are_random_and_url_safe()
    {
        var a = TokenGenerator.NewToken();
        var b = TokenGenerator.NewToken();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 40);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
    }

    [Fact]
    public void Token_comparison()
    {
        Assert.True(TokenGenerator.FixedTimeEquals("abc", "abc"));
        Assert.False(TokenGenerator.FixedTimeEquals("abd", "abc"));
        Assert.False(TokenGenerator.FixedTimeEquals(null, "abc"));
    }

    [Fact]
    public void Session_store_round_trips_and_only_deletes_its_own_session()
    {
        var path = Path.Combine(TestEnvironment.Home, "session-test.json");
        var store = new SessionStore(path);
        var session = new SessionInfo(5555, "tok", 42, DateTime.UtcNow);

        store.Write(session);
        Assert.Equal(session, store.Read());

        store.Delete(processId: 7); // another process: keep it
        Assert.NotNull(store.Read());

        store.Delete(processId: 42);
        Assert.Null(store.Read());
    }
}
