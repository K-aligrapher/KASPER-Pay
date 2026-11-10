using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Approvals;
using Kasper.Application.Commerce;
using Kasper.Application.Tools;
using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Domain.Rules;
using Kasper.Infrastructure.Persistence;
using Kasper.Infrastructure.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Commerce;

/// <summary>
/// commerce.purchase: buy one catalog product with PayPal. The model only names a product id (and
/// a reason). Price, currency, merchant and total come from the catalog in code, are bound into the
/// approval fingerprint, and are re-checked against the spending rules right before PayPal is called.
/// </summary>
public sealed class PurchaseTool(
    ICatalog catalog,
    IPayPalOrders paypal,
    IBrowserLauncher browser,
    KasperDbContext db,
    IClock clock,
    IOptionsMonitor<PaymentOptions> options,
    ILogger<PurchaseTool> log) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "commerce.purchase", 1,
        "Buy ONE product from the demo store with PayPal (asks you first, then PayPal asks again). Pick productId from: "
        + string.Join("; ", catalog.All.Select(p => $"{p.Id} = {p.Name}")) + ". Never invent ids, prices or totals.",
        EffectClass.ExternalWrite, WritesOnlyToStaging: false,
        [
            new ToolParameter("productId", ParamType.Text, true, "A catalog product id.", 64),
            new ToolParameter("quantity", ParamType.Integer, false, $"How many (1-{Guardrails.MaxQuantity}, default 1)."),
            new ToolParameter("reason", ParamType.Text, false, "One sentence on why this product fits the request.", 300),
        ],
        ["orderId", "status", "total"], TimeSpan.FromMinutes(10), MaxAttempts: 1, Idempotent: false, Integration: "paypal");

    private static int Quantity(JsonObject input) => Math.Max(1, Input.Int(input, "quantity", 1));

    public JsonObject? FingerprintExtras(JsonObject input)
    {
        var product = catalog.Find(Input.OptStr(input, "productId") ?? "");
        if (product is null) return new JsonObject { ["product"] = "unknown" };
        var q = Quantity(input);
        return new JsonObject
        {
            ["product"] = product.Id,
            ["merchant"] = product.Merchant,
            ["currency"] = product.Currency,
            ["unitAmount"] = Money.Format(product.Price),
            ["total"] = Money.Format(product.Price * q),
        };
    }

    public async Task<IReadOnlyList<ScreenViolation>> ScreenAsync(JsonObject input, CancellationToken ct)
    {
        var violations = await EvaluateAsync(input, excludeStep: null, ct);
        if (violations.Count > 0)
        {
            // Keep a record of what was refused, even though no workflow exists yet.
            var product = catalog.Find(Input.OptStr(input, "productId") ?? "");
            if (product is not null)
            {
                var q = Quantity(input);
                var blocked = Payment.Propose(null, null, product.Id, product.Name, product.Merchant, q, product.Price,
                    product.Currency, Input.OptStr(input, "reason"), "", clock.UtcNow);
                blocked.Block(violations[0].Rule, violations[0].Message, clock.UtcNow);
                db.Payments.Add(blocked);
                await db.SaveChangesAsync(ct);
            }
            log.LogInformation("Purchase blocked by {Rule}", violations[0].Rule);
        }
        return violations;
    }

    private async Task<IReadOnlyList<ScreenViolation>> EvaluateAsync(JsonObject input, Guid? excludeStep, CancellationToken ct)
    {
        var product = catalog.Find(Input.OptStr(input, "productId") ?? "");
        var since = clock.UtcNow.Date; // daily limit is per UTC day
        var window = clock.UtcNow.AddMinutes(-Math.Max(0, options.CurrentValue.DuplicateWindowMinutes));
        var from = since < window ? since : window;
        var rows = await db.Payments.AsNoTracking()
            .Where(p => p.CreatedAtUtc >= from && (excludeStep == null || p.StepId != excludeStep))
            .ToListAsync(ct);
        var history = rows.Where(p => PaymentStateMachine.CountsAsSpend(p.Status))
            .Select(p => new SpendRecord(p.ProductId, p.Amount, p.CreatedAtUtc)).ToList();
        return Guardrails.Evaluate(product, Quantity(input), options.CurrentValue, history, clock.UtcNow);
    }

    public async Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var product = catalog.Find(Input.OptStr(input, "productId") ?? "")
            ?? throw new ToolException("unknown_product", "That product is not in the catalog.", ErrorCategory.Validation);
        var q = Quantity(input);
        var violations = await EvaluateAsync(input, context.StepId, ct);
        var fingerprint = ActionHasher.Compute(Descriptor.Id, Descriptor.Version, input, null, FingerprintExtras(input));

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.StepId == context.StepId, ct);
        if (payment is null)
        {
            db.Payments.Add(Payment.Propose(context.WorkflowId, context.StepId, product.Id, product.Name, product.Merchant, q,
                product.Price, product.Currency, Input.OptStr(input, "reason"), fingerprint, clock.UtcNow));
            await db.SaveChangesAsync(ct);
        }
        else if (payment.Status == PaymentStatus.Proposed && payment.Fingerprint != fingerprint)
        {
            payment.Refresh(fingerprint, clock.UtcNow);
            await db.SaveChangesAsync(ct);
        }

        var total = Money.Format(product.Price * q);
        var fields = new List<PreviewField>
        {
            new("Item", q > 1 ? $"{product.Name} × {q}" : product.Name),
            new("Merchant", product.Merchant),
            new("Total", $"{total} {product.Currency}"),
            new("Pay with", "PayPal (sandbox)"),
        };
        if (Input.OptStr(input, "reason") is { } reason) fields.Add(new("Why this item", reason));

        var warnings = new List<string> { "Nothing is charged yet. After you approve here, PayPal opens and asks you to approve again." };
        warnings.AddRange(violations.Select(v => $"Blocked by {v.Rule}: {v.Message}"));
        return new ToolPreview($"Buy {product.Name} for {total} {product.Currency}", $"PayPal › {product.Merchant}", fields, null, warnings);
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var product = catalog.Find(Input.OptStr(input, "productId") ?? "")
            ?? throw new ToolException("unknown_product", "That product is not in the catalog.", ErrorCategory.Validation);
        var q = Quantity(input);
        var now = clock.UtcNow;

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.StepId == context.StepId, ct)
            ?? Payment.Propose(context.WorkflowId, context.StepId, product.Id, product.Name, product.Merchant, q, product.Price,
                product.Currency, Input.OptStr(input, "reason"),
                ActionHasher.Compute(Descriptor.Id, Descriptor.Version, input, null, FingerprintExtras(input)), now);
        if (db.Entry(payment).State == EntityState.Detached) db.Payments.Add(payment);

        // Replays must never create a second order (F13).
        if (payment.PayPalOrderId is not null)
            return Result(payment);

        // The amount in the approved fingerprint must still be what the catalog says.
        var expected = ActionHasher.Compute(Descriptor.Id, Descriptor.Version, input, null, FingerprintExtras(input));
        if (payment.Fingerprint != expected)
            throw new ToolException("fingerprint_mismatch", "The purchase changed after it was proposed. Nothing was charged.", ErrorCategory.Permanent);

        // Re-check the rules at the last moment, with everything else that happened in between.
        var violations = await EvaluateAsync(input, context.StepId, ct);
        if (violations.Count > 0)
        {
            payment.Block(violations[0].Rule, violations[0].Message, now);
            await db.SaveChangesAsync(CancellationToken.None);
            throw new ToolException("blocked_" + violations[0].Rule, $"Blocked: {violations[0].Message}", ErrorCategory.Permanent);
        }

        if (payment.Status == PaymentStatus.Proposed) payment.TransitionTo(PaymentStatus.Approved, now);
        await db.SaveChangesAsync(CancellationToken.None); // persist before any side effect

        try
        {
            var order = await paypal.CreateOrderAsync(context.IdempotencyKey, payment.Id.ToString("N"), product, q, ct);
            payment.CheckoutCreated(order.Id, order.ApproveUrl, clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);

            if (order.ApproveUrl is not null)
            {
                try { browser.Open(order.ApproveUrl); }
                catch (Exception ex) { log.LogWarning(ex, "Could not open the PayPal page for order {OrderId}", order.Id); }
            }
            return Result(payment);
        }
        catch (ToolException ex)
        {
            payment.Fail(ex.Message, clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static ToolResult Result(Payment p) => new(new JsonObject
    {
        ["orderId"] = p.PayPalOrderId,
        ["status"] = p.Status.ToString(),
        ["total"] = $"{Money.Format(p.Amount)} {p.Currency}",
    });
}
