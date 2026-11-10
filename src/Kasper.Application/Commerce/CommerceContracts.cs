using System.Globalization;

namespace Kasper.Application.Commerce;

/// <summary>PayPal credentials and environment. Values come from user secrets or env vars, never the repo.</summary>
public sealed class PayPalOptions
{
    public const string Section = "PayPal";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>"sandbox" (default) or "live". Live is out of scope for the hackathon build.</summary>
    public string Environment { get; set; } = "sandbox";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public string BaseUrl => string.Equals(Environment, "live", StringComparison.OrdinalIgnoreCase)
        ? "https://api-m.paypal.com"
        : "https://api-m.sandbox.paypal.com";
}

/// <summary>Spending guardrail settings (non-secret). Enforced in code, never by the model.</summary>
public sealed class PaymentOptions
{
    public const string Section = "Payments";

    public string Currency { get; set; } = "USD";
    public decimal PerTransactionLimit { get; set; } = 100.00m;
    public decimal DailyLimit { get; set; } = 250.00m;
    public int DuplicateWindowMinutes { get; set; } = 10;

    /// <summary>How often to ask PayPal whether the buyer approved, and how long to wait before giving up.</summary>
    public int PollIntervalSeconds { get; set; } = 3;
    public int PollTimeoutMinutes { get; set; } = 8;

    /// <summary>
    /// Spending mandate: purchases up to this amount skip KASPER's Final Review (PayPal still asks you).
    /// 0 turns auto-approval off. Weekly cap limits the total auto-approved per rolling 7 days.
    /// </summary>
    public decimal AutoApproveUnder { get; set; }
    public decimal AutoApproveWeeklyCap { get; set; } = 50.00m;

    /// <summary>Merchants KASPER may pay. Empty means "every merchant in the catalog".</summary>
    public List<string> AllowedMerchants { get; set; } = [];

    /// <summary>Invoice recipients KASPER may bill. Empty means any well-formed email.</summary>
    public List<string> AllowedRecipients { get; set; } = [];
}

/// <summary>A seeded catalog product. Prices exist only here; the model can only pick an id.</summary>
public sealed record Product(string Id, string Name, string Description, decimal Price, string Merchant, string Currency = "USD", string? ImageUrl = null, string? Url = null);

public interface ICatalog
{
    IReadOnlyList<Product> All { get; }
    Product? Find(string productId);
}

/// <summary>Address of the local app (set at startup). PayPal sends the buyer back here after checkout.</summary>
public static class LocalEndpoint
{
    public static string? BaseUrl { get; set; }
}

public static class Money
{
    /// <summary>PayPal wants amounts as strings with exactly two decimals and a dot separator.</summary>
    public static string Format(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    public static bool TryParse(string? value, out decimal amount) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
}

public sealed record PayPalOrder(string Id, string Status, string? ApproveUrl, string? CurrencyCode, decimal? Amount);

public sealed record PayPalCapture(string OrderId, string Status, string? CaptureId, string? CurrencyCode, decimal? Amount);

public interface IPayPalOrders
{
    /// <summary>Creates a CAPTURE order. <paramref name="requestId"/> makes retries safe (PayPal-Request-Id).</summary>
    Task<PayPalOrder> CreateOrderAsync(string requestId, string referenceId, Product product, int quantity, CancellationToken ct);

    Task<PayPalOrder> GetOrderAsync(string orderId, CancellationToken ct);

    /// <summary>Captures an APPROVED order. Safe to call twice: the same request id returns the same capture.</summary>
    Task<PayPalCapture> CaptureAsync(string orderId, string requestId, CancellationToken ct);
}

/// <summary>Opens the PayPal approval page in the user's browser. Only PayPal https links are allowed.</summary>
public interface IBrowserLauncher
{
    void Open(string url);
}

public sealed record PayPalInvoiceDraft(string Id, string? Href);

public interface IPayPalInvoices
{
    Task<PayPalInvoiceDraft> CreateDraftAsync(string recipientEmail, string description, decimal amount, string currency, CancellationToken ct);
    Task SendAsync(string invoiceId, CancellationToken ct);
    Task<PayPalInvoiceStatus> GetStatusAsync(string invoiceId, CancellationToken ct);
    Task RemindAsync(string invoiceId, string note, CancellationToken ct);
}

public sealed record PayPalInvoiceStatus(string Status, decimal? DueAmount);

/// <summary>Channel3 (live product search) settings. The key comes from user secrets or env vars only.</summary>
public sealed class Channel3Options
{
    public const string Section = "Channel3";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.trychannel3.com";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>Turns a product id from a plan (seeded id or quote id) into a priced product, in code.</summary>
public interface IProductResolver
{
    Task<Product?> ResolveAsync(string productId, CancellationToken ct);
}

/// <summary>Searches for products and freezes each result as a quote so its price can't change or be invented.</summary>
public interface IProductFinder
{
    Task<IReadOnlyList<Product>> FindAsync(string query, decimal? maxPrice, int take, CancellationToken ct);
}
