using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Commerce;
using Kasper.Application.Tools;
using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Commerce;
using Kasper.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kasper.UnitTests;

public class SplitMathTests
{
    [Theory]
    [InlineData(84, 3)]
    [InlineData(100, 3)]
    [InlineData(10, 7)]
    [InlineData(2.01, 2)]
    public void Shares_always_add_up_to_the_total_exactly(double total, int people)
    {
        var shares = InvoiceSplitTool.Shares((decimal)total, people);
        Assert.Equal(people, shares.Count);
        Assert.Equal((decimal)total, shares.Sum());
        Assert.True(shares.Max() - shares.Min() <= 0.01m); // fair: nobody pays more than a cent extra
    }
}

public class Channel3ParseTests
{
    [Fact]
    public void Picks_the_cheapest_in_stock_usd_offer_and_ignores_the_rest()
    {
        var json = JsonNode.Parse("""
        {"products":[
          {"id":"p1","title":"Quiet Mouse","brands":[{"id":"b","name":"Acme"}],
           "images":[{"url":"https://img/x.png","is_main_image":true}],
           "offers":[
             {"domain":"pricey.com","url":"https://pricey.com/m","price":{"price":29.99,"currency":"USD"},"availability":"InStock"},
             {"domain":"cheap.com","url":"https://cheap.com/m","price":{"price":19.5,"currency":"USD"},"availability":"InStock"},
             {"domain":"gone.com","url":"https://gone.com/m","price":{"price":5,"currency":"USD"},"availability":"OutOfStock"},
             {"domain":"euro.com","url":"https://euro.com/m","price":{"price":3,"currency":"EUR"},"availability":"InStock"}]},
          {"id":"p2","title":"Sold Out Thing","offers":[{"domain":"x.com","url":"u","price":{"price":1,"currency":"USD"},"availability":"OutOfStock"}]}
        ]}
        """)!.AsObject();
        var hit = Assert.Single(Channel3Client.Parse(json));
        Assert.Equal("cheap.com", hit.Domain);
        Assert.Equal(19.50m, hit.Price);
        Assert.Equal("Acme", hit.Brand);
        Assert.Equal("https://img/x.png", hit.ImageUrl);
    }
}

public sealed class WowFeatureTests : IDisposable
{
    private sealed class Clock : IClock { public DateTime UtcNow { get; set; } = new(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc); }
    private sealed class Opts<T>(T v) : IOptionsMonitor<T> { public T CurrentValue => v; public T Get(string? n) => v; public IDisposable? OnChange(Action<T, string?> l) => null; }
    private sealed class Resolver(params Product[] items) : IProductResolver
    {
        public Task<Product?> ResolveAsync(string id, CancellationToken ct) => Task.FromResult(items.FirstOrDefault(p => p.Id == id));
    }
    private sealed class Browser : IBrowserLauncher { public void Open(string url) { } }
    private sealed class Orders : IPayPalOrders
    {
        public int Created;
        public Task<PayPalOrder> CreateOrderAsync(string r, string i, Product p, int q, CancellationToken ct) { Created++; return Task.FromResult(new PayPalOrder("O" + Created, "CREATED", "https://sandbox.paypal.com/x", "USD", null)); }
        public Task<PayPalOrder> GetOrderAsync(string id, CancellationToken ct) => Task.FromResult(new PayPalOrder(id, "APPROVED", null, "USD", null));
        public Task<PayPalCapture> CaptureAsync(string id, string r, CancellationToken ct) => Task.FromResult(new PayPalCapture(id, "COMPLETED", "CAP-" + id, "USD", Amount));
        public decimal Amount = 4.50m;
    }
    private sealed class Invoices : IPayPalInvoices
    {
        public int Drafts, Sends, Reminders;
        public string Status = "SENT";
        public Task<PayPalInvoiceDraft> CreateDraftAsync(string e, string d, decimal a, string c, CancellationToken ct) { Drafts++; return Task.FromResult(new PayPalInvoiceDraft("INV-" + Drafts, null)); }
        public Task SendAsync(string id, CancellationToken ct) { Sends++; return Task.CompletedTask; }
        public Task<PayPalInvoiceStatus> GetStatusAsync(string id, CancellationToken ct) => Task.FromResult(new PayPalInvoiceStatus(Status, null));
        public Task RemindAsync(string id, string note, CancellationToken ct) { Reminders++; return Task.CompletedTask; }
    }

    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly KasperDbContext _db;
    private readonly Clock _clock = new();
    private readonly Orders _orders = new();
    private readonly Invoices _invoices = new();
    private readonly PaymentOptions _opts = new() { PollIntervalSeconds = 0, PollTimeoutMinutes = 1, AutoApproveUnder = 5m, AutoApproveWeeklyCap = 8m };
    private readonly Product _pen = new("q_pen", "Pen", "", 4.50m, "shop.example");
    private readonly Product _lamp = new("q_lamp", "Lamp", "", 40m, "shop.example");

    public WowFeatureTests()
    {
        _conn.Open();
        _db = new KasperDbContext(new DbContextOptionsBuilder<KasperDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private PurchaseTool Purchase() => new(new Resolver(_pen, _lamp), _orders, new Browser(), _db, _clock, new Opts<PaymentOptions>(_opts), NullLogger<PurchaseTool>.Instance);
    private InvoiceRemindTool Remind() => new(_db, _invoices, _clock, new Opts<PaymentOptions>(_opts));
    private InvoiceSplitTool Split() => new(_invoices, _db, _clock, new Opts<PaymentOptions>(_opts));
    private static ToolContext Ctx(Guid? wf = null) => new(wf ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"), 1);
    private static JsonObject Buy(string id) => new() { ["productId"] = id };

    // ---- mandates

    [Fact]
    public async Task Small_purchase_inside_the_mandate_is_pre_approved_and_marked_as_such()
    {
        var t = Purchase();
        Assert.NotNull(await t.TryAutoApproveAsync(Buy("q_pen"), default));
        var r = await t.ExecuteAsync(Ctx(), Buy("q_pen"), default); // no review card was ever shown
        Assert.Equal("Captured", (string?)r.Output["status"]);
        var row = await _db.Payments.SingleAsync();
        Assert.True(row.AutoApproved);
    }

    [Fact]
    public async Task Bigger_purchases_still_need_review_and_cannot_run_unreviewed()
    {
        var t = Purchase();
        Assert.Null(await t.TryAutoApproveAsync(Buy("q_lamp"), default));
        var ex = await Assert.ThrowsAsync<ToolException>(() => t.ExecuteAsync(Ctx(), Buy("q_lamp"), default));
        Assert.Equal("not_reviewed", ex.Code);
        Assert.Equal(0, _orders.Created);
    }

    [Fact]
    public async Task Mandate_is_off_by_default_and_respects_the_weekly_cap()
    {
        _opts.AutoApproveUnder = 0;
        Assert.Null(await Purchase().TryAutoApproveAsync(Buy("q_pen"), default));

        _opts.AutoApproveUnder = 5m; _opts.DuplicateWindowMinutes = 0;
        var t = Purchase();
        await t.ExecuteAsync(Ctx(), Buy("q_pen"), default); // 4.50 of 8.00 used
        Assert.Null(await t.TryAutoApproveAsync(Buy("q_pen"), default)); // another 4.50 would pass the 8.00 cap
    }

    [Fact]
    public async Task Mandate_never_bypasses_the_guardrails()
    {
        _opts.PerTransactionLimit = 3m; // pen costs 4.50
        Assert.Null(await Purchase().TryAutoApproveAsync(Buy("q_pen"), default));
    }

    // ---- reminders

    private async Task<Payment> SentInvoiceAsync(string email = "alex@example.com", string amount = "200")
    {
        var tool = new InvoiceTool(_invoices, _db, _clock, new Opts<PaymentOptions>(_opts), NullLogger<InvoiceTool>.Instance);
        var input = new JsonObject { ["recipient"] = email, ["amount"] = amount, ["description"] = "Logo" };
        var c = Ctx();
        await tool.PreviewAsync(c, input, default);
        await tool.ExecuteAsync(c, input, default);
        return await _db.Payments.OrderByDescending(p => p.CreatedAtUtc).FirstAsync();
    }

    [Fact]
    public async Task Reminder_goes_out_once_for_an_unpaid_invoice_then_is_rate_limited()
    {
        await SentInvoiceAsync();
        var input = new JsonObject { ["recipient"] = "alex@example.com" };
        var t = Remind();
        Assert.Empty(await t.ScreenAsync(input, default));
        await t.ExecuteAsync(Ctx(), input, default);
        Assert.Equal(1, _invoices.Reminders);

        Assert.Contains(await t.ScreenAsync(input, default), v => v.Rule == "reminder_too_soon");
        await Assert.ThrowsAsync<ToolException>(() => t.ExecuteAsync(Ctx(), input, default));
        Assert.Equal(1, _invoices.Reminders);
    }

    [Fact]
    public async Task Paid_invoices_never_get_a_reminder()
    {
        await SentInvoiceAsync();
        _invoices.Status = "PAID";
        var input = new JsonObject { ["recipient"] = "alex@example.com" };
        Assert.Contains(await Remind().ScreenAsync(input, default), v => v.Rule == "already_paid");
        await Assert.ThrowsAsync<ToolException>(() => Remind().ExecuteAsync(Ctx(), input, default));
        Assert.Equal(0, _invoices.Reminders);
    }

    [Fact]
    public async Task Reminder_for_someone_never_invoiced_is_refused()
    {
        var v = await Remind().ScreenAsync(new JsonObject { ["recipient"] = "nobody@example.com" }, default);
        Assert.Contains(v, x => x.Rule == "no_open_invoice");
    }

    // ---- split

    private static JsonObject SplitInput(string people = "a@x.com, b@x.com, c@x.com", string total = "84") =>
        new() { ["recipients"] = people, ["total"] = total, ["description"] = "Dinner" };

    [Fact]
    public async Task Split_sends_one_invoice_per_person_and_replays_send_nothing_new()
    {
        var wf = Guid.NewGuid();
        var t = Split();
        var preview = await t.PreviewAsync(Ctx(wf), SplitInput(), default);
        Assert.Contains("28.00", preview.Content);
        await t.ExecuteAsync(Ctx(wf), SplitInput(), default);
        await t.ExecuteAsync(Ctx(wf), SplitInput(), default);
        Assert.Equal((3, 3), (_invoices.Drafts, _invoices.Sends));
        Assert.Equal(84m, (await _db.Payments.Where(p => p.Status == PaymentStatus.Sent).ToListAsync()).Sum(p => p.Amount));
    }

    [Theory]
    [InlineData("a@x.com", "84", "invalid_recipients")]
    [InlineData("a@x.com, nope", "84", "invalid_recipients")]
    [InlineData("a@x.com, b@x.com", "1", "invalid_amount")]
    [InlineData("a@x.com, b@x.com", "abc", "invalid_amount")]
    public async Task Bad_splits_are_blocked_up_front(string people, string total, string rule)
    {
        Assert.Contains(await Split().ScreenAsync(SplitInput(people, total), default), v => v.Rule == rule);
        Assert.Equal(0, _invoices.Drafts);
    }

    [Fact]
    public async Task Split_fingerprint_changes_if_anyone_or_any_amount_changes()
    {
        var t = Split();
        var a = (await t.FingerprintExtrasAsync(SplitInput(), default))!.ToJsonString();
        Assert.NotEqual(a, (await t.FingerprintExtrasAsync(SplitInput(total: "90"), default))!.ToJsonString());
        Assert.NotEqual(a, (await t.FingerprintExtrasAsync(SplitInput(people: "a@x.com, b@x.com, d@x.com"), default))!.ToJsonString());
    }

    // ---- receipts

    [Fact]
    public async Task Receipts_chain_together_and_editing_an_old_record_breaks_the_chain()
    {
        _opts.DuplicateWindowMinutes = 0;
        var t = Purchase();
        await t.ExecuteAsync(Ctx(), Buy("q_pen"), default);
        _opts.AutoApproveWeeklyCap = 100m;
        await t.ExecuteAsync(Ctx(), Buy("q_pen"), default);
        await SentInvoiceAsync();

        var ok = await ReceiptChain.VerifyAsync(_db, default);
        Assert.True(ok.Intact);
        Assert.Equal(3, ok.Count);
        var rows = await _db.Payments.Where(p => p.ReceiptSeq != null).OrderBy(p => p.ReceiptSeq).ToListAsync();
        Assert.Equal(Receipts.Genesis, rows[0].PrevReceiptHash);
        Assert.Equal(rows[0].ReceiptHash, rows[1].PrevReceiptHash);

        // Someone quietly changes the first amount in the database.
        await _db.Database.ExecuteSqlRawAsync("UPDATE Payments SET Amount = 1 WHERE ReceiptSeq = 1");
        _db.ChangeTracker.Clear();
        var bad = await ReceiptChain.VerifyAsync(_db, default);
        Assert.False(bad.Intact);
        Assert.Equal(1, bad.BrokenAtSeq);
    }

    [Fact]
    public async Task Only_finished_payments_are_sealed()
    {
        var c = Ctx();
        await Purchase().PreviewAsync(c, Buy("q_lamp"), default); // proposed, never paid
        Assert.Equal(0, (await ReceiptChain.VerifyAsync(_db, default)).Count);
    }
}
