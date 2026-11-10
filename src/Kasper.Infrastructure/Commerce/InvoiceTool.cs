using System.Net.Mail;
using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Approvals;
using Kasper.Application.Commerce;
using Kasper.Application.Tools;
using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Persistence;
using Kasper.Infrastructure.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Commerce;

/// <summary>
/// commerce.invoice: bill someone through PayPal Invoicing v2. The invoice is drafted and sent only after
/// Final Review, and the recipient, amount and currency are bound into the approval fingerprint.
/// </summary>
public sealed class InvoiceTool(
    IPayPalInvoices invoices,
    KasperDbContext db,
    IClock clock,
    IOptionsMonitor<PaymentOptions> options,
    ILogger<InvoiceTool> log) : ITool
{
    public const decimal MaxAmount = 10_000m;

    public ToolDescriptor Descriptor { get; } = new(
        "commerce.invoice", 1,
        "Send a PayPal invoice to someone (asks you first). Use the amount the user stated, as plain digits like 200 or 49.50.",
        EffectClass.ExternalWrite, WritesOnlyToStaging: false,
        [
            new ToolParameter("recipient", ParamType.Text, true, "The recipient's email address.", 320),
            new ToolParameter("amount", ParamType.Text, true, "Amount in USD, digits only, e.g. 200 or 49.50.", 12),
            new ToolParameter("description", ParamType.Text, true, "What the invoice is for, e.g. logo design.", 200),
        ],
        ["invoiceId", "status"], TimeSpan.FromMinutes(2), MaxAttempts: 1, Idempotent: false, Integration: "paypal");

    private static string? Email(JsonObject input)
    {
        var raw = Input.OptStr(input, "recipient")?.Trim();
        if (raw is null || raw.Length > 320 || raw.Contains(' ')) return null;
        return MailAddress.TryCreate(raw, out var a) && a.Address == raw && raw.Contains('.', StringComparison.Ordinal) && raw.IndexOf('@') > 0
            ? raw.ToLowerInvariant() : null;
    }

    private static decimal? Amount(JsonObject input) =>
        Money.TryParse(Input.OptStr(input, "amount")?.Trim().TrimStart('$'), out var a) && a > 0 && a <= MaxAmount
            ? decimal.Round(a, 2) : null;

    public Task<JsonObject?> FingerprintExtrasAsync(JsonObject input, CancellationToken ct) => Task.FromResult<JsonObject?>(new JsonObject
    {
        ["recipient"] = Email(input) ?? "invalid",
        ["amount"] = Amount(input) is { } a ? Money.Format(a) : "invalid",
        ["currency"] = options.CurrentValue.Currency,
    });

    public async Task<IReadOnlyList<ScreenViolation>> ScreenAsync(JsonObject input, CancellationToken ct)
    {
        var v = await EvaluateAsync(input, null, ct);
        if (v.Count > 0)
        {
            if (Email(input) is { } email && Amount(input) is { } amount)
            {
                var blocked = Payment.ProposeInvoice(null, null, email, Input.OptStr(input, "description") ?? "Invoice", amount,
                    options.CurrentValue.Currency, "", clock.UtcNow);
                blocked.Block(v[0].Rule, v[0].Message, clock.UtcNow);
                db.Payments.Add(blocked);
                await db.SaveChangesAsync(ct);
            }
            log.LogInformation("Invoice blocked by {Rule}", v[0].Rule);
        }
        return v;
    }

    private async Task<IReadOnlyList<ScreenViolation>> EvaluateAsync(JsonObject input, Guid? excludeStep, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var v = new List<ScreenViolation>();
        var email = Email(input);
        var amount = Amount(input);
        if (email is null) v.Add(new("invalid_recipient", "That is not a valid email address."));
        if (amount is null) v.Add(new("invalid_amount", $"The amount must be between 0.01 and {Money.Format(MaxAmount)} USD."));
        if (string.IsNullOrWhiteSpace(Input.OptStr(input, "description"))) v.Add(new("invalid_description", "The invoice needs a description."));
        if (!string.Equals(o.Currency, "USD", StringComparison.OrdinalIgnoreCase)) v.Add(new(Guardrails.CurrencyNotAllowed, "Only USD invoices are allowed."));
        if (email is not null && o.AllowedRecipients.Count > 0 && !o.AllowedRecipients.Contains(email, StringComparer.OrdinalIgnoreCase))
            v.Add(new("recipient_allowlist", $"{email} is not on your allowed recipients list."));

        if (email is not null && amount is not null && o.DuplicateWindowMinutes > 0)
        {
            var since = clock.UtcNow.AddMinutes(-o.DuplicateWindowMinutes);
            var dup = await db.Payments.AsNoTracking().AnyAsync(p => p.Kind == "invoice" && p.Recipient == email && p.Amount == amount
                && p.CreatedAtUtc >= since && (p.Status == PaymentStatus.Approved || p.Status == PaymentStatus.Sent)
                && (excludeStep == null || p.StepId != excludeStep), ct);
            if (dup) v.Add(new(Guardrails.DuplicatePurchase, $"You already invoiced {email} for this amount in the last {o.DuplicateWindowMinutes} minutes."));
        }
        return v;
    }

    public async Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var v = await EvaluateAsync(input, context.StepId, ct);
        var email = Email(input);
        var amount = Amount(input);
        if (email is null || amount is null)
            throw new ToolException(v.FirstOrDefault()?.Rule ?? "invalid_invoice", v.FirstOrDefault()?.Message ?? "Invalid invoice.", ErrorCategory.Validation);
        var description = Input.Str(input, "description");
        var fingerprint = ActionHasher.Compute(Descriptor.Id, Descriptor.Version, input, null, await FingerprintExtrasAsync(input, ct));

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.StepId == context.StepId, ct);
        if (payment is null)
        {
            db.Payments.Add(Payment.ProposeInvoice(context.WorkflowId, context.StepId, email, description, amount.Value, options.CurrentValue.Currency, fingerprint, clock.UtcNow));
            await db.SaveChangesAsync(ct);
        }
        else if (payment.Status == PaymentStatus.Proposed && payment.Fingerprint != fingerprint)
        {
            payment.Refresh(fingerprint, clock.UtcNow);
            await db.SaveChangesAsync(ct);
        }

        var total = Money.Format(amount.Value);
        var warnings = new List<string> { "Nothing is sent yet. The invoice is created and emailed by PayPal only after you approve." };
        var past = await db.Payments.AsNoTracking().Where(p => p.Kind == "invoice" && p.Status == PaymentStatus.Sent).ToListAsync(ct);
        if (!past.Any(p => p.Recipient == email)) warnings.Add($"Heads-up: you have never invoiced {email} before.");
        if (past.Count >= 3 && amount.Value > 3 * past.Average(p => p.Amount))
            warnings.Add($"Heads-up: this is more than 3× your usual invoice (about ${Money.Format(past.Average(p => p.Amount))}).");
        warnings.AddRange(v.Select(x => $"Blocked by {x.Rule}: {x.Message}"));
        return new ToolPreview($"Invoice {email} for {total} USD", $"PayPal Invoicing › {email}",
            [new("To", email), new("For", description), new("Total", $"{total} {options.CurrentValue.Currency}"), new("Sent with", "PayPal Invoicing (sandbox)")],
            null, warnings);
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var email = Email(input);
        var amount = Amount(input);
        if (email is null || amount is null)
            throw new ToolException("invalid_invoice", "The invoice details are not valid.", ErrorCategory.Validation);
        var description = Input.Str(input, "description");
        var now = clock.UtcNow;
        var fingerprint = ActionHasher.Compute(Descriptor.Id, Descriptor.Version, input, null, await FingerprintExtrasAsync(input, ct));

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.StepId == context.StepId, ct)
            ?? Payment.ProposeInvoice(context.WorkflowId, context.StepId, email, description, amount.Value, options.CurrentValue.Currency, fingerprint, now);
        if (db.Entry(payment).State == EntityState.Detached) db.Payments.Add(payment);

        if (payment.Status == PaymentStatus.Sent) return Result(payment); // replay: never send twice
        if (payment.Fingerprint != fingerprint)
            throw new ToolException("fingerprint_mismatch", "The invoice changed after it was proposed. Nothing was sent.", ErrorCategory.Permanent);

        var violations = await EvaluateAsync(input, context.StepId, ct);
        if (violations.Count > 0 && payment.PayPalInvoiceId is null)
        {
            payment.Block(violations[0].Rule, violations[0].Message, now);
            await db.SaveChangesAsync(CancellationToken.None);
            throw new ToolException("blocked_" + violations[0].Rule, $"Blocked: {violations[0].Message}", ErrorCategory.Permanent);
        }

        if (payment.Status == PaymentStatus.Proposed) payment.TransitionTo(PaymentStatus.Approved, now);
        await db.SaveChangesAsync(CancellationToken.None);

        try
        {
            if (payment.PayPalInvoiceId is null)
            {
                var draft = await invoices.CreateDraftAsync(email, description, amount.Value, options.CurrentValue.Currency, ct);
                payment.AttachInvoice(draft.Id);
                await db.SaveChangesAsync(CancellationToken.None); // remember the draft before sending
            }
            await invoices.SendAsync(payment.PayPalInvoiceId!, ct);
            payment.Sent(payment.PayPalInvoiceId!, clock.UtcNow);
            payment.RecordInvoiceState("SENT", clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            await ReceiptChain.SealAsync(db, payment, CancellationToken.None);
            return Result(payment);
        }
        catch (ToolException ex)
        {
            payment.Fail(ex.Message, clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static ToolResult Result(Payment p) => new(new JsonObject { ["invoiceId"] = p.PayPalInvoiceId, ["status"] = p.Status.ToString() });
}
