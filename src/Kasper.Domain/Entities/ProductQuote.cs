namespace Kasper.Domain.Entities;

/// <summary>
/// A product found by code (never by the model), frozen with the price seen at that moment.
/// The purchase tool buys a quote id, so the approved price can't drift or be invented.
/// </summary>
public sealed class ProductQuote
{
    private ProductQuote() { }

    /// <summary>"q_" + guid. This is what the plan passes around as productId.</summary>
    public string Id { get; private set; } = "";
    public string Source { get; private set; } = "";
    public string SourceProductId { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string? Brand { get; private set; }
    public string Merchant { get; private set; } = "";
    public string? Url { get; private set; }
    public string? ImageUrl { get; private set; }
    public decimal Price { get; private set; }
    public string Currency { get; private set; } = "USD";
    public DateTime CreatedAtUtc { get; private set; }

    public static ProductQuote Create(string source, string sourceProductId, string title, string? brand, string merchant,
        string? url, string? imageUrl, decimal price, string currency, DateTime nowUtc) => new()
    {
        Id = "q_" + Guid.NewGuid().ToString("N"),
        Source = source,
        SourceProductId = sourceProductId,
        Title = title.Length > 200 ? title[..200] : title,
        Brand = brand,
        Merchant = merchant.Length > 200 ? merchant[..200] : merchant,
        Url = url is { Length: > 1000 } ? null : url,
        ImageUrl = imageUrl is { Length: > 1000 } ? null : imageUrl,
        Price = price,
        Currency = currency,
        CreatedAtUtc = nowUtc,
    };
}
