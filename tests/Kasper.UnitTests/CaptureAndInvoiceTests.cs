using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Commerce;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Commerce;
using Kasper.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kasper.UnitTests;

public sealed class CaptureAndInvoiceTests : IDisposable
{
    private sealed class Clock : IClock { public DateTime UtcNow { get; set; } = new(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc); }
    private sealed class Opts<T>(T v) : IOptionsMonitor<T> { public T CurrentValue => v; public T Get(string? n) => v; public IDisposable? OnChange(Action<T, string?> l) => null; }
    private sealed class Catalog : ICatalog, IProductResolver
    {
        public Task<Product?> ResolveAsync(string id, CancellationToken ct) => Task.FromResult(Find(id));
        public IReadOnlyList<Product> All { get; } = [new("kb", "Keyboard", "", 79.99m, "Shop")];
        public Product? Find(string id) => All.FirstOrDefault(p => p.Id == id);
    }
    private sealed class Browser : IBrowserLauncher { public void Open(string url) { } }
    private sealed class Orders : IPayPalOrders
    {
        public Queue<string> Statuses = new(["CREATED", "CREATED", "APPROVED"]);
        public PayPalCapture Capture = new("O1", "COMPLETED", "CAP", "USD", 79.99m);
        public int Captures;
        public Task<PayPalOrder> CreateOrderAsync(string r, string i, Product p, int q, CancellationToken ct) =>
            Task.FromResult(new PayPalOrder("O1", "CREATED", "https://sandbox.paypal.com/x", "USD", null));
        public Task<PayPalOrder> GetOrderAsync(string id, CancellationToken ct) =>
            Task.FromResult(new PayPalOrder(id, Statuses.Count > 1 ? Statuses.Dequeue() : Statuses.Peek(), null, "USD", null));
        public Task<PayPalCapture> CaptureAsync(string id, string r, CancellationToken ct) { Captures++; return Task.FromResult(Capture); }
    }
    private sealed class Invoices : IPayPalInvoices
    {
        public int Drafts, Sends;
        public Task<PayPalInvoiceDraft> CreateDraftAsync(string e, string d, decimal a, string c, CancellationToken ct)
        { Drafts++; return Task.FromResult(new PayPalInvoiceDraft("INV-1", null)); }
        public Task SendAsync(string id, CancellationToken ct) { Sends++; return Task.CompletedTask; }
        public string Status = "SENT";
        public int Reminders;
        public Task<PayPalInvoiceStatus> GetStatusAsync(string id, CancellationToken ct) => Task.FromResult(new PayPalInvoiceStatus(Status, null));
        public Task RemindAsync(string id, string note, CancellationToken ct) { Reminders++; return Task.CompletedTask; }
    }

    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly KasperDbContext _db;
    private readonly Orders _orders = new();
    private readonly Invoices _invoices = new();
    private readonly Clock _clock = new();
    private readonly PaymentOptions _opts = new() { PollIntervalSeconds = 0, PollTimeoutMinutes = 1 };

    public CaptureAndInvoiceTests()
    {
        _conn.Open();
        _db = new KasperDbContext(new DbContextOptionsBuilder<KasperDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private PurchaseTool Purchase() => new(new Catalog(), _orders, new Browser(), _db, _clock, new Opts<PaymentOptions>(_opts), NullLogger<PurchaseTool>.Instance);
    private InvoiceTool Invoice() => new(_invoices, _db, _clock, new Opts<PaymentOptions>(_opts), NullLogger<InvoiceTool>.Instance);
    private static ToolContext Ctx() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"), 1);
    private static JsonObject Item() => new() { ["productId"] = "kb" };
    private static JsonObject Inv(string email = "alex@example.com", string amount = "200") =>
        new() { ["recipient"] = email, ["amount"] = amount, ["description"] = "Logo design" };

    [Fact]
    public async Task Capture_happens_only_after_the_buyer_approves_in_paypal()
    {
        var t = Purchase(); var c = Ctx();
        await t.PreviewAsync(c, Item(), default);
        var r = await t.ExecuteAsync(c, Item(), default);
        Assert.Equal("Captured", (string?)r.Output["status"]);
        Assert.Equal(1, _orders.Captures);
    }

    [Fact]
    public async Task A_capture_that_does_not_match_the_approved_amount_is_a_failure()
    {
        _orders.Capture = new("O1", "COMPLETED", "CAP", "USD", 1.00m);
        var t = Purchase(); var c = Ctx();
        await t.PreviewAsync(c, Item(), default);
        var ex = await Assert.ThrowsAsync<ToolException>(() => t.ExecuteAsync(c, Item(), default));
        Assert.Equal("capture_mismatch", ex.Code);
        Assert.Equal(PaymentStatus.Failed, (await _db.Payments.SingleAsync()).Status);
    }

    [Fact]
    public async Task Voided_order_is_cancelled_and_nothing_is_captured()
    {
        _orders.Statuses = new(["VOIDED"]);
        var t = Purchase(); var c = Ctx();
        await t.PreviewAsync(c, Item(), default);
        await Assert.ThrowsAsync<ToolException>(() => t.ExecuteAsync(c, Item(), default));
        Assert.Equal(PaymentStatus.Cancelled, (await _db.Payments.SingleAsync()).Status);
        Assert.Equal(0, _orders.Captures);
    }

    [Fact]
    public async Task Never_approved_order_expires_after_the_timeout()
    {
        _orders.Statuses = new(["CREATED"]);
        _opts.PollTimeoutMinutes = 0;
        var t = Purchase(); var c = Ctx();
        await t.PreviewAsync(c, Item(), default);
        await Assert.ThrowsAsync<ToolException>(() => t.ExecuteAsync(c, Item(), default));
        Assert.Equal(PaymentStatus.Expired, (await _db.Payments.SingleAsync()).Status);
        Assert.Equal(0, _orders.Captures);
    }

    [Fact]
    public async Task Invoice_is_drafted_and_sent_once_even_if_replayed()
    {
        var t = Invoice(); var c = Ctx();
        var p = await t.PreviewAsync(c, Inv(), default);
        Assert.Contains(p.Fields, f => f.Label == "Total" && f.Value == "200.00 USD");
        await t.ExecuteAsync(c, Inv(), default);
        await t.ExecuteAsync(c, Inv(), default);
        Assert.Equal((1, 1), (_invoices.Drafts, _invoices.Sends));
        Assert.Equal(PaymentStatus.Sent, (await _db.Payments.SingleAsync()).Status);
    }

    [Fact]
    public async Task Invoice_changed_after_approval_is_refused()
    {
        var t = Invoice(); var c = Ctx();
        await t.PreviewAsync(c, Inv(), default);
        var ex = await Assert.ThrowsAsync<ToolException>(() => t.ExecuteAsync(c, Inv(amount: "2000"), default));
        Assert.Equal("fingerprint_mismatch", ex.Code);
        Assert.Equal(0, _invoices.Sends);
    }

    [Theory]
    [InlineData("not-an-email", "200", "invalid_recipient")]
    [InlineData("a@b.co", "-5", "invalid_amount")]
    [InlineData("a@b.co", "999999", "invalid_amount")]
    [InlineData("a@b.co", "abc", "invalid_amount")]
    public async Task Bad_invoices_are_blocked_up_front(string email, string amount, string rule)
    {
        var v = await Invoice().ScreenAsync(Inv(email, amount), default);
        Assert.Contains(v, x => x.Rule == rule);
        Assert.Equal(0, _invoices.Drafts);
    }

    [Fact]
    public async Task Invoice_recipient_allowlist_is_enforced_when_set()
    {
        _opts.AllowedRecipients = ["boss@example.com"];
        Assert.Contains(await Invoice().ScreenAsync(Inv(), default), x => x.Rule == "recipient_allowlist");
        Assert.Empty(await Invoice().ScreenAsync(Inv("boss@example.com"), default));
    }
}
