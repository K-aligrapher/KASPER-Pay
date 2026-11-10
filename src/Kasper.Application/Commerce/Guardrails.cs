using Kasper.Application.Tools;

namespace Kasper.Application.Commerce;

/// <summary>A payment already made or in flight, used for daily-limit and duplicate checks.</summary>
public sealed record SpendRecord(string ProductId, decimal Amount, DateTime AtUtc);

/// <summary>
/// Spending rules, enforced in code (PayPal PRD F5). The model never sees or changes these.
/// Pure: pass in the history and the clock, get the broken rules back.
/// </summary>
public static class Guardrails
{
    public const string UnknownProduct = "unknown_product";
    public const string InvalidQuantity = "invalid_quantity";
    public const string CurrencyNotAllowed = "currency_not_allowed";
    public const string PerTransactionLimit = "per_transaction_limit";
    public const string DailyLimit = "daily_limit";
    public const string MerchantNotAllowed = "merchant_allowlist";
    public const string DuplicatePurchase = "duplicate_purchase";

    public const int MaxQuantity = 10;

    public static IReadOnlyList<ScreenViolation> Evaluate(
        Product? product, int quantity, PaymentOptions o, IReadOnlyCollection<SpendRecord> history, DateTime nowUtc)
    {
        if (product is null)
            return [new(UnknownProduct, "That product is not in the catalog.")];

        var v = new List<ScreenViolation>();
        if (quantity is < 1 or > MaxQuantity)
            v.Add(new(InvalidQuantity, $"Quantity must be between 1 and {MaxQuantity}."));

        if (!string.Equals(product.Currency, o.Currency, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(product.Currency, "USD", StringComparison.OrdinalIgnoreCase))
            v.Add(new(CurrencyNotAllowed, $"Only {o.Currency} payments are allowed."));

        if (o.AllowedMerchants.Count > 0 && !o.AllowedMerchants.Contains(product.Merchant, StringComparer.OrdinalIgnoreCase))
            v.Add(new(MerchantNotAllowed, $"{product.Merchant} is not on your allowed merchants list."));

        if (quantity is >= 1 and <= MaxQuantity)
        {
            var total = product.Price * quantity;
            if (total > o.PerTransactionLimit)
                v.Add(new(PerTransactionLimit,
                    $"${Money.Format(total)} is over your ${Money.Format(o.PerTransactionLimit)} per-purchase limit."));

            var today = history.Where(h => h.AtUtc.Date == nowUtc.Date).Sum(h => h.Amount);
            if (today + total > o.DailyLimit)
                v.Add(new(DailyLimit,
                    $"This would bring today's spending to ${Money.Format(today + total)}, over your ${Money.Format(o.DailyLimit)} daily limit."));

            var window = TimeSpan.FromMinutes(Math.Max(0, o.DuplicateWindowMinutes));
            if (window > TimeSpan.Zero && history.Any(h => h.ProductId == product.Id && nowUtc - h.AtUtc < window))
                v.Add(new(DuplicatePurchase,
                    $"You already bought {product.Name} in the last {o.DuplicateWindowMinutes} minutes."));
        }
        return v;
    }
}
