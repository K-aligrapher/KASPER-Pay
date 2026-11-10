using System.Security.Cryptography;
using System.Text;
using Kasper.Domain.Entities;

namespace Kasper.Application.Commerce;

/// <summary>
/// Tamper-evident receipts. Every completed payment or sent invoice is sealed with a hash of its facts
/// plus the previous receipt's hash, so editing or deleting an earlier record breaks every later hash.
/// </summary>
public static class Receipts
{
    public const string Genesis = "genesis";

    public static string Compute(Payment p, long seq, string prevHash)
    {
        var text = string.Join('|', "kasper-receipt-v1", seq, p.Id, p.Kind, p.ProductId, p.ProductName, p.Merchant, p.Recipient ?? "",
            Money.Format(p.Amount), p.Currency, p.Fingerprint, p.PayPalOrderId ?? "", p.PayPalCaptureId ?? "", p.PayPalInvoiceId ?? "",
            p.Status, prevHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

public sealed record ReceiptDto(long Seq, Guid PaymentId, string Kind, string Item, string? Recipient, string Amount, string Currency,
    string Status, string? PayPalReference, string PrevHash, string Hash);

public sealed record ChainStatus(bool Intact, int Count, long? BrokenAtSeq, string? Head);
