using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Commerce;
using Kasper.Application.Tools;
using Kasper.Infrastructure.Commerce;
using Kasper.Infrastructure.PayPal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kasper.UnitTests;

public class PayPalTests
{
    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            var (status, json) = respond(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static IOptionsMonitor<PayPalOptions> Opts(string id = "id", string secret = "secret") =>
        new StaticMonitor<PayPalOptions>(new PayPalOptions { ClientId = id, ClientSecret = secret });

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static string TokenJson(int expiresIn = 3600, string token = "tok-1") =>
        $$"""{"access_token":"{{token}}","expires_in":{{expiresIn}},"token_type":"Bearer"}""";

    [Fact]
    public async Task Token_is_cached_until_close_to_expiry_then_refreshed()
    {
        var clock = new FakeClock();
        var n = 0;
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK, TokenJson(token: "tok-" + ++n)));
        var provider = new PayPalTokenProvider(new HttpClient(handler), Opts(), clock);

        Assert.Equal("tok-1", await provider.GetTokenAsync(default));
        Assert.Equal("tok-1", await provider.GetTokenAsync(default));
        Assert.Single(handler.Calls);

        clock.UtcNow = clock.UtcNow.AddSeconds(3600 - 30); // inside the 60 s refresh window
        Assert.Equal("tok-2", await provider.GetTokenAsync(default));
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Token_request_uses_basic_auth_against_sandbox_by_default()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK, TokenJson()));
        var provider = new PayPalTokenProvider(new HttpClient(handler), Opts("abc", "xyz"), new FakeClock());
        await provider.GetTokenAsync(default);

        var req = handler.Calls[0].Request;
        Assert.StartsWith("https://api-m.sandbox.paypal.com/", req.RequestUri!.ToString());
        Assert.Equal("Basic", req.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("abc:xyz")), req.Headers.Authorization.Parameter);
        Assert.Contains("grant_type=client_credentials", handler.Calls[0].Body);
    }

    [Fact]
    public async Task Missing_credentials_fail_clearly_without_calling_paypal()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.OK, TokenJson()));
        var provider = new PayPalTokenProvider(new HttpClient(handler), Opts("", ""), new FakeClock());
        var ex = await Assert.ThrowsAsync<ToolException>(() => provider.GetTokenAsync(default));
        Assert.Equal("paypal_not_configured", ex.Code);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void Live_environment_is_only_used_when_asked_for()
    {
        Assert.Equal("https://api-m.sandbox.paypal.com", new PayPalOptions().BaseUrl);
        Assert.Equal("https://api-m.paypal.com", new PayPalOptions { Environment = "live" }.BaseUrl);
    }

    private static PayPalOrdersService Orders(FakeHandler handler) =>
        new(new HttpClient(handler), new PayPalTokenProvider(new HttpClient(new FakeHandler((_, _) => (HttpStatusCode.OK, TokenJson()))), Opts(), new FakeClock()),
            Opts(), NullLogger<PayPalOrdersService>.Instance);

    [Fact]
    public async Task Create_order_takes_amount_from_the_catalog_and_sends_a_request_id()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.Created, """
            {"id":"ORDER1","status":"PAYER_ACTION_REQUIRED","purchase_units":[{"amount":{"currency_code":"USD","value":"159.98"}}],
             "links":[{"rel":"self","href":"https://x/self"},{"rel":"payer-action","href":"https://sandbox.paypal.com/checkoutnow?token=ORDER1"}]}
            """));
        var product = new SeedCatalog().Find("kb-mech-01")!;

        var order = await Orders(handler).CreateOrderAsync("req-1", "intent-1", product, 2, default);

        Assert.Equal("ORDER1", order.Id);
        Assert.Equal("https://sandbox.paypal.com/checkoutnow?token=ORDER1", order.ApproveUrl);
        var call = handler.Calls[0];
        Assert.Equal("req-1", call.Request.Headers.GetValues("PayPal-Request-Id").Single());
        var body = JsonNode.Parse(call.Body!)!;
        Assert.Equal("CAPTURE", (string?)body["intent"]);
        Assert.Equal("159.98", (string?)body["purchase_units"]![0]!["amount"]!["value"]);
        Assert.Equal("USD", (string?)body["purchase_units"]![0]!["amount"]!["currency_code"]);
    }

    [Fact]
    public async Task Create_order_rejects_absurd_quantities()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.Created, "{}"));
        var product = new SeedCatalog().Find("ms-wl-01")!;
        await Assert.ThrowsAsync<ToolException>(() => Orders(handler).CreateOrderAsync("r", "i", product, 0, default));
        await Assert.ThrowsAsync<ToolException>(() => Orders(handler).CreateOrderAsync("r", "i", product, 11, default));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Capture_returns_the_captured_amount_for_verification()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.Created, """
            {"id":"ORDER1","status":"COMPLETED","purchase_units":[{"payments":{"captures":[{"id":"CAP1","status":"COMPLETED","amount":{"currency_code":"USD","value":"79.99"}}]}}]}
            """));
        var cap = await Orders(handler).CaptureAsync("ORDER1", "cap-req-1", default);

        Assert.Equal("COMPLETED", cap.Status);
        Assert.Equal("CAP1", cap.CaptureId);
        Assert.Equal("USD", cap.CurrencyCode);
        Assert.Equal(79.99m, cap.Amount);
        Assert.Equal("cap-req-1", handler.Calls[0].Request.Headers.GetValues("PayPal-Request-Id").Single());
    }

    [Fact]
    public async Task Paypal_errors_are_classified_without_leaking_the_response_body()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.UnprocessableEntity,
            """{"name":"UNPROCESSABLE_ENTITY","details":[{"issue":"ORDER_NOT_APPROVED"}],"debug_id":"x","secret":"leak-me"}"""));
        var ex = await Assert.ThrowsAsync<ToolException>(() => Orders(handler).CaptureAsync("ORDER1", "r", default));
        Assert.Equal("paypal_order_not_approved", ex.Code);
        Assert.DoesNotContain("leak-me", ex.Message);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task Server_errors_and_rate_limits_are_retryable()
    {
        var h500 = new FakeHandler((_, _) => (HttpStatusCode.BadGateway, "{}"));
        Assert.True((await Assert.ThrowsAsync<ToolException>(() => Orders(h500).GetOrderAsync("O", default))).Retryable);
        var h429 = new FakeHandler((_, _) => (HttpStatusCode.TooManyRequests, "{}"));
        Assert.True((await Assert.ThrowsAsync<ToolException>(() => Orders(h429).GetOrderAsync("O", default))).Retryable);
    }

    [Fact]
    public async Task A_401_clears_the_token_and_retries_once()
    {
        var n = 0;
        var tokenHandler = new FakeHandler((_, _) => (HttpStatusCode.OK, TokenJson(token: "tok-" + ++n)));
        var tokens = new PayPalTokenProvider(new HttpClient(tokenHandler), Opts(), new FakeClock());
        var handler = new FakeHandler((req, _) => req.Headers.Authorization!.Parameter == "tok-1"
            ? (HttpStatusCode.Unauthorized, "{}")
            : (HttpStatusCode.OK, """{"id":"O","status":"CREATED"}"""));
        var svc = new PayPalOrdersService(new HttpClient(handler), tokens, Opts(), NullLogger<PayPalOrdersService>.Instance);

        Assert.Equal("CREATED", (await svc.GetOrderAsync("O", default)).Status);
        Assert.Equal(2, tokenHandler.Calls.Count);
    }

    // ---- catalog (M2) ----

    [Fact]
    public void Catalog_is_seeded_with_unique_usd_products_and_positive_prices()
    {
        var catalog = new SeedCatalog();
        Assert.InRange(catalog.All.Count, 5, 10);
        Assert.Equal(catalog.All.Count, catalog.All.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(catalog.All, p =>
        {
            Assert.Equal("USD", p.Currency);
            Assert.True(p.Price > 0);
            Assert.Equal(p.Price, decimal.Round(p.Price, 2));
        });
    }

    [Fact]
    public void Catalog_lookup_is_exact_by_id_and_unknown_ids_are_null()
    {
        var catalog = new SeedCatalog();
        Assert.NotNull(catalog.Find("kb-mech-01"));
        Assert.NotNull(catalog.Find(" KB-MECH-01 "));
        Assert.Null(catalog.Find("kb-mech-99"));
        Assert.Null(catalog.Find(""));
    }

    [Theory]
    [InlineData(79.99, "79.99")]
    [InlineData(80, "80.00")]
    [InlineData(0.005, "0.01")]
    public void Money_formats_with_two_decimals_and_a_dot(double amount, string expected) =>
        Assert.Equal(expected, Money.Format((decimal)amount));

    // ---- optional live sandbox test: skipped unless credentials are in user secrets / env ----

    [Fact]
    public async Task Sandbox_can_create_and_read_an_order_when_credentials_exist()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets(typeof(Kasper.Api.KasperApiHost).Assembly, optional: true)
            .AddEnvironmentVariables("KASPER__")
            .Build();
        var o = config.GetSection(PayPalOptions.Section).Get<PayPalOptions>();
        if (o is not { IsConfigured: true } || !string.Equals(o.Environment, "sandbox", StringComparison.OrdinalIgnoreCase))
            return; // no sandbox credentials on this machine: nothing to test

        var http = new HttpClient();
        var tokens = new PayPalTokenProvider(http, new StaticMonitor<PayPalOptions>(o), new FakeClock { UtcNow = DateTime.UtcNow });
        var svc = new PayPalOrdersService(http, tokens, new StaticMonitor<PayPalOptions>(o), NullLogger<PayPalOrdersService>.Instance);

        var order = await svc.CreateOrderAsync(Guid.NewGuid().ToString("N"), "test", new SeedCatalog().Find("ms-wl-01")!, 1, default);
        Assert.False(string.IsNullOrEmpty(order.Id));
        Assert.NotNull(order.ApproveUrl);
        // PayPal's create response is minimal (no amount); the full order comes from GET.

        var again = await svc.GetOrderAsync(order.Id, default);
        Assert.Equal(order.Id, again.Id);
        Assert.Equal(24.99m, again.Amount);
        Assert.Equal("USD", again.CurrencyCode);
        Assert.NotEqual("COMPLETED", again.Status);
    }
}
