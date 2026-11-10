using Kasper.Application.Commerce;
using Kasper.Domain.Entities;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kasper.Infrastructure.Commerce;

/// <summary>Seals completed payments and invoices into a hash chain, and checks the chain later.</summary>
public static class ReceiptChain
{
    public static async Task SealAsync(KasperDbContext db, Payment p, CancellationToken ct)
    {
        if (p.ReceiptHash is not null) return;
        var last = await db.Payments.AsNoTracking().Where(x => x.ReceiptSeq != null)
            .OrderByDescending(x => x.ReceiptSeq).Select(x => new { x.ReceiptSeq, x.ReceiptHash }).FirstOrDefaultAsync(ct);
        var seq = (last?.ReceiptSeq ?? 0) + 1;
        var prev = last?.ReceiptHash ?? Receipts.Genesis;
        p.Seal(seq, prev, Receipts.Compute(p, seq, prev));
        await db.SaveChangesAsync(ct);
    }

    public static async Task<ChainStatus> VerifyAsync(KasperDbContext db, CancellationToken ct)
    {
        var sealedRows = await db.Payments.AsNoTracking().Where(x => x.ReceiptSeq != null).OrderBy(x => x.ReceiptSeq).ToListAsync(ct);
        var prev = Receipts.Genesis;
        long expectedSeq = 1;
        foreach (var p in sealedRows)
        {
            if (p.ReceiptSeq != expectedSeq || p.PrevReceiptHash != prev || p.ReceiptHash != Receipts.Compute(p, p.ReceiptSeq!.Value, prev))
                return new ChainStatus(false, sealedRows.Count, p.ReceiptSeq, prev);
            prev = p.ReceiptHash!;
            expectedSeq++;
        }
        return new ChainStatus(true, sealedRows.Count, null, sealedRows.Count == 0 ? null : prev);
    }

    public static async Task<IReadOnlyList<ReceiptDto>> ListAsync(KasperDbContext db, CancellationToken ct) =>
        (await db.Payments.AsNoTracking().Where(x => x.ReceiptSeq != null).OrderByDescending(x => x.ReceiptSeq).Take(200).ToListAsync(ct))
        .Select(p => new ReceiptDto(p.ReceiptSeq!.Value, p.Id, p.Kind, p.ProductName, p.Recipient, Money.Format(p.Amount), p.Currency,
            p.Status.ToString(), p.PayPalCaptureId ?? p.PayPalInvoiceId, p.PrevReceiptHash!, p.ReceiptHash!)).ToList();
}
