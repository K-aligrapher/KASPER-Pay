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

    /// <summary>Merchants KASPER may pay. Empty means "every merchant in the catalog".</summary>
    public List<string> AllowedMerchants { get; set; } = [];
}

/// <summary>A seeded catalog product. Prices exist only here; the model can only pick an id.</summary>
public sealed record Product(string Id, string Name, string Description, decimal Price, string Merchant, string Currency = "USD");

public interface ICatalog
{
    IReadOnlyList<Product> All { get; }
    Product? Find(string productId);
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
