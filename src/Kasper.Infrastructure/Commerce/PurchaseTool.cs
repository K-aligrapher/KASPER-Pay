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
/// commerce.purchase: buy ONE product with PayPal. The model only passes a product id (from commerce.find_product).
/// Price, currency, merchant and total come from code, are bound into the approval fingerprint, and are re-checked
/// against the spending rules right before PayPal is called. A standing mandate can pre-approve small purchases,
/// but PayPal's own approval by the buyer is always required.
/// </summary>
public sealed class PurchaseTool(
    IProductResolver products,
    IPayPalOrders paypal,
    IBrowserLauncher browser,
    KasperDbContext db,
    IClock clock,
    IOptionsMonitor<PaymentOptions> options,
    ILogger<PurchaseTool> log) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "commerce.purchase", 1,
        "Buy ONE product with PayPal (asks you first, then PayPal asks again). productId must be \"$s1.productId\" from a commerce.find_product step. Never invent ids, prices or totals.",
        EffectClass.ExternalWrite, WritesOnlyToStaging: false,
        [
            new ToolParameter("productId", ParamType.Text, true, "The productId output of commerce.find_product, e.g. $s1.productId.", 64),
            new ToolParameter("quantity", ParamType.Integer, false, $"How many (1-{Guardrails.MaxQuantity}, default 1)."),
            new ToolParameter("reason", ParamType.Text, false, "One sentence on why this product fits the request.", 300),
        ],
        ["orderId", "status", "total"], TimeSpan.FromMinutes(10), MaxAttempts: 1, Idempotent: false, Integration: "paypal");

    private static int Quantity(JsonObject input) => Math.Max(1, Input.Int(input, "quantity", 1));

    private Task<Product?> ProductAsync(JsonObject input, CancellationToken ct) =>
        products.ResolveAsync(Input.OptStr(input, "productId") ?? "", ct);

    public async Task<JsonObject?> FingerprintExtrasAsync(JsonObject input, CancellationToken ct)
    {
        var product = await ProductAsync(input, ct);
        if (product is null) return new JsonObject { ["product"] = "unknown" };
        var q = Quantity(input);
        return new JsonObject
        {
            ["product"] = product.Id,
            ["name"] = product.Name,
            ["merchant"] = product.Merchant,
            ["currency"] = product.Currency,
            ["unitAmount"] = Money.Format(product.Price),
            ["total"] = Money.Format(product.Price * q),
        };
    }

    private async Task<string> FingerprintAsync(JsonObject input, CancellationToken ct) =>
        ActionHasher.Compute(Descriptor.Id, Descriptor.Version, input, null, await FingerprintExtrasAsync(input, ct));

    public async Task<IReadOnlyList<ScreenViolation>> ScreenAsync(JsonObject input, CancellationToken ct)
    {
        var violations = await EvaluateAsync(input, excludeStep: null, ct);
        if (violations.Count > 0)
        {
            var product = await ProductAsync(input, ct);
            if (product is not null)
            {
                var blocked = Payment.Propose(null, null, product.Id, product.Name, product.Merchant, Quantity(input), product.Price,
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
        var product = await ProductAsync(input, ct);
        var since = clock.UtcNow.Date; // daily limit is per UTC day
        var window = clock.UtcNow.AddMinutes(-Math.Max(0, options.CurrentValue.DuplicateWindowMinutes));
        var from = since < window ? since : window;
        var rows = await db.Payments.AsNoTracking()
            .Where(p => p.CreatedAtUtc >= from && (excludeStep == null || p.StepId != excludeStep))
            .ToListAsync(ct);
        var history = rows.Where(p => p.Kind == "purchase" && PaymentStateMachine.CountsAsSpend(p.Status))
            .Select(p => new SpendRecord(p.ProductId, p.Amount, p.CreatedAtUtc)).ToList();
        return Guardrails.Evaluate(product, Quantity(input), options.CurrentValue, history, clock.UtcNow);
    }

    /// <summary>Spending mandate: small purchases inside your standing limits skip Final Review (PayPal still asks).</summary>
    public async Task<string?> TryAutoApproveAsync(JsonObject input, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (o.AutoApproveUnder <= 0) return null;
        var product = await ProductAsync(input, ct);
        if (product is null) return null;
        var total = product.Price * Quantity(input);
        if (total > o.AutoApproveUnder) return null;
        if ((await EvaluateAsync(input, null, ct)).Count > 0) return null;

        var weekAgo = clock.UtcNow.AddDays(-7);
        var spent = (await db.Payments.AsNoTracking().Where(p => p.AutoApproved && p.CreatedAtUtc >= weekAgo).ToListAsync(ct))
            .Where(p => PaymentStateMachine.CountsAsSpend(p.Status)).Sum(p => p.Amount);
        if (spent + total > o.AutoApproveWeeklyCap) return null;
        return $"Mandate: ${Money.Format(total)} is under your ${Money.Format(o.AutoApproveUnder)} auto-approve limit " +
               $"(${Money.Format(spent + total)} of ${Money.Format(o.AutoApproveWeeklyCap)} this week)";
    }

    /// <summary>Plain, code-computed warnings. Nothing here comes from the model.</summary>
    private async Task<List<string>> RiskSignalsAsync(Product product, decimal total, CancellationToken ct)
    {
        var signals = new List<string>();
        var done = await db.Payments.AsNoTracking().Where(p => p.Kind == "purchase" && p.Status == PaymentStatus.Captured).ToListAsync(ct);
        if (!done.Any(p => p.Merchant == product.Merchant))
            signals.Add($"First time buying from {product.Merchant}.");
        if (done.Count >= 3 && total > 3 * done.Average(p => p.Amount))
            signals.Add($"This is more than 3× your usual purchase (about ${Money.Format(done.Average(p => p.Amount))}).");
        var limit = options.CurrentValue.PerTransactionLimit;
        if (total > limit * 0.8m && total <= limit)
            signals.Add($"This is close to your ${Money.Format(limit)} per-purchase limit.");
        return signals;
    }

    public async Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var product = await ProductAsync(input, ct)
            ?? throw new ToolException("unknown_product", "That product is not available (or its price quote expired). Ask again.", ErrorCategory.Validation);
        var q = Quantity(input);
        var violations = await EvaluateAsync(input, context.StepId, ct);
        var fingerprint = await FingerprintAsync(input, ct);

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
        if (product.Url is not null) fields.Add(new("Product page", product.Url));
        if (Input.OptStr(input, "reason") is { } reason) fields.Add(new("Why this item", reason));

        var warnings = new List<string> { "Nothing is charged yet. After you approve here, PayPal opens and asks you to approve again." };
        warnings.AddRange((await RiskSignalsAsync(product, product.Price * q, ct)).Select(s => "Heads-up: " + s));
        warnings.AddRange(violations.Select(v => $"Blocked by {v.Rule}: {v.Message}"));
        return new ToolPreview($"Buy {product.Name} for {total} {product.Currency}", $"PayPal › {product.Merchant}", fields, null, warnings);
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var product = await ProductAsync(input, ct)
            ?? throw new ToolException("unknown_product", "That product is not available (or its price quote expired).", ErrorCategory.Validation);
        var q = Quantity(input);
        var now = clock.UtcNow;
        var fingerprint = await FingerprintAsync(input, ct);

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.StepId == context.StepId, ct);
        if (payment is null)
        {
            // No review card was shown, so this only runs when the standing mandate pre-approved it.
            var mandate = await TryAutoApproveAsync(input, ct)
                ?? throw new ToolException("not_reviewed", "This purchase was never reviewed. Nothing was charged.", ErrorCategory.Permanent);
            log.LogInformation("Auto-approved under mandate");
            payment = Payment.Propose(context.WorkflowId, context.StepId, product.Id, product.Name, product.Merchant, q, product.Price,
                product.Currency, Input.OptStr(input, "reason") ?? mandate, fingerprint, now);
            payment.MarkAutoApproved();
            db.Payments.Add(payment);
        }

        // Replays must never create a second order (F13).
        if (payment.PayPalOrderId is not null)
            return payment.Status is PaymentStatus.CheckoutCreated or PaymentStatus.PayerApproved
                ? await FinishAsync(payment, ct) // resume after a restart
                : Result(payment);

        // The amount in the approved fingerprint must still be what the store quoted.
        if (payment.Fingerprint != fingerprint)
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
            return await FinishAsync(payment, ct);
        }
        catch (ToolException ex) when (payment.Status == PaymentStatus.Approved)
        {
            payment.Fail(ex.Message, clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Waits for the buyer to approve in PayPal, then captures once and verifies the result (F8).</summary>
    private async Task<ToolResult> FinishAsync(Payment payment, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var deadline = clock.UtcNow.AddMinutes(o.PollTimeoutMinutes);
        while (payment.Status == PaymentStatus.CheckoutCreated)
        {
            var order = await paypal.GetOrderAsync(payment.PayPalOrderId!, ct);
            if (order.Status == "APPROVED")
            {
                payment.TransitionTo(PaymentStatus.PayerApproved, clock.UtcNow);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            else if (order.Status is "COMPLETED")
                break; // captured earlier; verify below via an idempotent capture call
            else if (order.Status is "VOIDED" || clock.UtcNow >= deadline)
            {
                payment.TransitionTo(order.Status == "VOIDED" ? PaymentStatus.Cancelled : PaymentStatus.Expired, clock.UtcNow);
                await db.SaveChangesAsync(CancellationToken.None);
                throw new ToolException("payment_not_approved", "The PayPal payment was not approved. Nothing was charged.", ErrorCategory.Permanent);
            }
            else await Task.Delay(TimeSpan.FromSeconds(o.PollIntervalSeconds), ct);
        }

        // Same request id every time: PayPal returns the same capture instead of charging twice.
        var cap = await paypal.CaptureAsync(payment.PayPalOrderId!, "cap-" + payment.Id.ToString("N"), ct);
        var expected = Money.Format(payment.Amount);
        if (cap.Status != "COMPLETED" || cap.CurrencyCode != payment.Currency || cap.Amount is not { } paid || Money.Format(paid) != expected)
        {
            payment.Fail($"PayPal capture did not match the approved purchase (status {cap.Status}).", clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            throw new ToolException("capture_mismatch", "PayPal's capture did not match what you approved. Check your PayPal account.", ErrorCategory.Permanent);
        }
        if (payment.Status == PaymentStatus.CheckoutCreated) payment.TransitionTo(PaymentStatus.PayerApproved, clock.UtcNow);
        payment.Captured(cap.CaptureId, clock.UtcNow);
        await db.SaveChangesAsync(CancellationToken.None);
        await ReceiptChain.SealAsync(db, payment, CancellationToken.None);
        log.LogInformation("Payment {PaymentId} captured for order {OrderId}", payment.Id, payment.PayPalOrderId);
        return Result(payment);
    }

    private static ToolResult Result(Payment p) => new(new JsonObject
    {
        ["orderId"] = p.PayPalOrderId,
        ["status"] = p.Status.ToString(),
        ["total"] = $"{Money.Format(p.Amount)} {p.Currency}",
    });
}
