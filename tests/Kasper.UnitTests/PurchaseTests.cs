using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Approvals;
using Kasper.Application.Commerce;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Kasper.Domain.Rules;
using Kasper.Infrastructure.Commerce;
using Kasper.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kasper.UnitTests;

public class GuardrailTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc);
    private static readonly Product Kb = new("kb", "Keyboard", "", 79.99m, "Shop");
    private static readonly PaymentOptions Defaults = new();

    private static string[] Rules(Product? p, int q = 1, PaymentOptions? o = null, params SpendRecord[] history) =>
        Guardrails.Evaluate(p, q, o ?? Defaults, history, Now).Select(v => v.Rule).ToArray();

    [Fact] public void A_normal_purchase_passes() => Assert.Empty(Rules(Kb));

    [Fact] public void Unknown_product_is_refused() => Assert.Equal([Guardrails.UnknownProduct], Rules(null));

    [Fact]
    public void Over_the_per_transaction_limit_is_blocked_by_name()
    {
        var monitor = Kb with { Id = "mon", Price = 900m };
        Assert.Contains(Guardrails.PerTransactionLimit, Rules(monitor));
        Assert.Contains(Guardrails.PerTransactionLimit, Rules(Kb, 2)); // 159.98 > 100
    }

    [Fact]
    public void Limit_is_inclusive_and_quantity_is_bounded()
    {
        Assert.Empty(Rules(Kb with { Price = 100m }));
        Assert.Contains(Guardrails.InvalidQuantity, Rules(Kb, 0));
        Assert.Contains(Guardrails.InvalidQuantity, Rules(Kb, 11));
    }

    [Fact]
    public void Daily_limit_counts_todays_spend_only()
    {
        var o = new PaymentOptions { DailyLimit = 100m, DuplicateWindowMinutes = 0 };
        Assert.Contains(Guardrails.DailyLimit, Rules(Kb, 1, o, new SpendRecord("other", 30m, Now.AddHours(-1))));
        Assert.Empty(Rules(Kb, 1, o, new SpendRecord("other", 30m, Now.AddDays(-1))));
    }

    [Fact]
    public void Duplicate_purchase_inside_the_window_is_blocked()
    {
        Assert.Contains(Guardrails.DuplicatePurchase, Rules(Kb, 1, null, new SpendRecord("kb", 79.99m, Now.AddMinutes(-3))));
        Assert.DoesNotContain(Guardrails.DuplicatePurchase, Rules(Kb, 1, null, new SpendRecord("kb", 79.99m, Now.AddMinutes(-30))));
    }

    [Fact]
    public void Merchant_allowlist_applies_only_when_set()
    {
        Assert.Empty(Rules(Kb));
        var o = new PaymentOptions { AllowedMerchants = ["Someone Else"] };
        Assert.Contains(Guardrails.MerchantNotAllowed, Rules(Kb, 1, o));
    }

    [Fact]
    public void Non_usd_is_refused()
    {
        Assert.Contains(Guardrails.CurrencyNotAllowed, Rules(Kb with { Currency = "EUR" }));
    }
}

public class PaymentStateTests
{
    [Fact]
    public void Happy_path_is_allowed_in_order()
    {
        PaymentStatus[] path = [PaymentStatus.Proposed, PaymentStatus.Approved, PaymentStatus.CheckoutCreated, PaymentStatus.PayerApproved, PaymentStatus.Captured];
        for (var i = 0; i < path.Length - 1; i++) Assert.True(PaymentStateMachine.CanTransition(path[i], path[i + 1]));
    }

    [Theory]
    [InlineData(PaymentStatus.Proposed, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Proposed, PaymentStatus.CheckoutCreated)]
    [InlineData(PaymentStatus.Approved, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.CheckoutCreated, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Captured, PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Blocked, PaymentStatus.Approved)]
    [InlineData(PaymentStatus.Rejected, PaymentStatus.Approved)]
    public void Skipping_steps_or_leaving_a_terminal_state_is_refused(PaymentStatus from, PaymentStatus to) =>
        Assert.False(PaymentStateMachine.CanTransition(from, to));

    [Fact]
    public void Entity_throws_on_an_invalid_transition()
    {
        var p = Kasper.Domain.Entities.Payment.Propose(null, null, "x", "X", "M", 1, 1m, "USD", null, "f", DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => p.Captured("cap", DateTime.UtcNow));
        Assert.Equal(PaymentStatus.Proposed, p.Status);
    }

    [Fact]
    public void Only_committed_states_count_as_spend()
    {
        Assert.True(PaymentStateMachine.CountsAsSpend(PaymentStatus.Captured));
        Assert.True(PaymentStateMachine.CountsAsSpend(PaymentStatus.CheckoutCreated));
        Assert.False(PaymentStateMachine.CountsAsSpend(PaymentStatus.Proposed));
        Assert.False(PaymentStateMachine.CountsAsSpend(PaymentStatus.Blocked));
        Assert.False(PaymentStateMachine.CountsAsSpend(PaymentStatus.Rejected));
    }
}

public sealed class PurchaseToolTests : IDisposable
{
    private sealed class Clock : IClock { public DateTime UtcNow { get; set; } = new(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc); }

    private sealed class FakeCatalog(params Product[] products) : ICatalog
    {
        public List<Product> Items { get; } = [.. products];
        public IReadOnlyList<Product> All => Items;
        public Product? Find(string id) => Items.FirstOrDefault(p => p.Id == id);
    }

    private sealed class FakePayPal : IPayPalOrders
    {
        public int Created;
        public Task<PayPalOrder> CreateOrderAsync(string requestId, string referenceId, Product product, int quantity, CancellationToken ct)
        {
            Created++;
            return Task.FromResult(new PayPalOrder("ORDER-" + Created, "PAYER_ACTION_REQUIRED", "https://sandbox.paypal.com/checkoutnow?token=T", "USD", product.Price * quantity));
        }
        public Task<PayPalOrder> GetOrderAsync(string orderId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PayPalCapture> CaptureAsync(string orderId, string requestId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeBrowser : IBrowserLauncher { public List<string> Opened { get; } = []; public void Open(string url) => Opened.Add(url); }

    private sealed class StaticOptions(PaymentOptions v) : IOptionsMonitor<PaymentOptions>
    {
        public PaymentOptions CurrentValue => v;
        public PaymentOptions Get(string? name) => v;
        public IDisposable? OnChange(Action<PaymentOptions, string?> l) => null;
    }

    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly KasperDbContext _db;
    private readonly Clock _clock = new();
    private readonly FakeCatalog _catalog = new(
        new Product("kb", "Keyboard", "", 79.99m, "Shop"),
        new Product("mon", "Monitor", "", 900m, "Shop"));
    private readonly FakePayPal _paypal = new();
    private readonly FakeBrowser _browser = new();
    private readonly PurchaseTool _tool;

    public PurchaseToolTests()
    {
        _conn.Open();
        _db = new KasperDbContext(new DbContextOptionsBuilder<KasperDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _tool = new PurchaseTool(_catalog, _paypal, _browser, _db, _clock,
            new StaticOptions(new PaymentOptions()), NullLogger<PurchaseTool>.Instance);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static JsonObject Input(string id, int? qty = null) =>
        new() { ["productId"] = id, ["quantity"] = qty, ["reason"] = "fits the budget" };

    private static ToolContext Ctx() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"), 1);

    [Fact]
    public async Task Over_limit_purchase_is_blocked_before_anything_reaches_paypal()
    {
        var v = await _tool.ScreenAsync(Input("mon"), default);
        Assert.Equal(Guardrails.PerTransactionLimit, v[0].Rule); // reported first
        Assert.Equal(0, _paypal.Created);
        var row = await _db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Blocked, row.Status);
        Assert.Equal(Guardrails.PerTransactionLimit, row.BlockedRule);
    }

    [Fact]
    public async Task Unknown_product_is_refused_and_not_recorded()
    {
        var v = await _tool.ScreenAsync(Input("nope"), default);
        Assert.Equal(Guardrails.UnknownProduct, Assert.Single(v).Rule);
        Assert.Empty(await _db.Payments.ToListAsync());
    }

    [Fact]
    public async Task Preview_shows_catalog_total_and_a_nothing_charged_note()
    {
        var p = await _tool.PreviewAsync(Ctx(), Input("kb"), default);
        Assert.Contains(p.Fields, f => f.Label == "Total" && f.Value == "79.99 USD");
        Assert.Contains(p.Warnings!, w => w.Contains("Nothing is charged yet"));
        Assert.Equal(PaymentStatus.Proposed, (await _db.Payments.SingleAsync()).Status);
        Assert.Equal(0, _paypal.Created);
    }

    [Fact]
    public void Fingerprint_changes_when_the_catalog_price_changes()
    {
        var a = ActionHasher.Compute("commerce.purchase", 1, Input("kb"), null, _tool.FingerprintExtras(Input("kb")));
        _catalog.Items[0] = _catalog.Items[0] with { Price = 1.00m };
        var b = ActionHasher.Compute("commerce.purchase", 1, Input("kb"), null, _tool.FingerprintExtras(Input("kb")));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task A_price_change_after_approval_blocks_execution_and_charges_nothing()
    {
        var ctx = Ctx();
        await _tool.PreviewAsync(ctx, Input("kb"), default);
        _catalog.Items[0] = _catalog.Items[0] with { Price = 5.00m };

        var ex = await Assert.ThrowsAsync<ToolException>(() => _tool.ExecuteAsync(ctx, Input("kb"), default));
        Assert.Equal("fingerprint_mismatch", ex.Code);
        Assert.Equal(0, _paypal.Created);
        Assert.Empty(_browser.Opened);
    }

    [Fact]
    public async Task Execute_creates_one_order_opens_paypal_and_replays_do_not_duplicate()
    {
        var ctx = Ctx();
        await _tool.PreviewAsync(ctx, Input("kb"), default);

        var first = await _tool.ExecuteAsync(ctx, Input("kb"), default);
        var second = await _tool.ExecuteAsync(ctx, Input("kb"), default);

        Assert.Equal(1, _paypal.Created);
        Assert.Single(_browser.Opened);
        Assert.Equal("ORDER-1", (string?)first.Output["orderId"]);
        Assert.Equal("ORDER-1", (string?)second.Output["orderId"]);
        var row = await _db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.CheckoutCreated, row.Status);
        Assert.Equal("ORDER-1", row.PayPalOrderId);
    }

    [Fact]
    public async Task Rules_are_rechecked_at_execution_time()
    {
        // Two proposals for the same item are both previewed, then both approved.
        var c1 = Ctx();
        var c2 = Ctx();
        await _tool.PreviewAsync(c1, Input("kb"), default);
        await _tool.PreviewAsync(c2, Input("kb"), default);

        await _tool.ExecuteAsync(c1, Input("kb"), default);
        var ex = await Assert.ThrowsAsync<ToolException>(() => _tool.ExecuteAsync(c2, Input("kb"), default));

        Assert.Equal("blocked_" + Guardrails.DuplicatePurchase, ex.Code);
        Assert.Equal(1, _paypal.Created);
        Assert.Equal(1, await _db.Payments.CountAsync(p => p.Status == PaymentStatus.Blocked));
    }

    [Fact]
    public async Task Daily_limit_stops_the_purchase_that_would_cross_it()
    {
        _catalog.Items.Add(new Product("a", "A", "", 80m, "Shop"));
        _catalog.Items.Add(new Product("b", "B", "", 80m, "Shop"));
        _catalog.Items.Add(new Product("c", "C", "", 80m, "Shop"));
        foreach (var id in new[] { "a", "b", "c" })
        {
            var ctx = Ctx();
            await _tool.PreviewAsync(ctx, Input(id), default);
            await _tool.ExecuteAsync(ctx, Input(id), default);
        }
        // 240 spent today; another 79.99 would cross the 250 daily limit.
        var v = await _tool.ScreenAsync(Input("kb"), default);
        Assert.Contains(v, x => x.Rule == Guardrails.DailyLimit);
    }

    [Fact]
    public void Browser_only_opens_paypal_https_links()
    {
        Assert.True(BrowserLauncher.IsAllowed("https://www.sandbox.paypal.com/checkoutnow?token=A"));
        Assert.False(BrowserLauncher.IsAllowed("http://www.paypal.com/x"));
        Assert.False(BrowserLauncher.IsAllowed("https://paypal.com.evil.example/x"));
        Assert.False(BrowserLauncher.IsAllowed("file:///C:/Windows/System32/calc.exe"));
    }
}
