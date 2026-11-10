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
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Commerce;

/// <summary>Keeps the local copy of every sent invoice's PayPal state up to date. Shared by the tool and the UI refresh.</summary>
public sealed class InvoiceTracker(KasperDbContext db, IPayPalInvoices invoices, IClock clock)
{
    public static bool IsOpen(string? state) => state is not ("PAID" or "MARKED_AS_PAID" or "CANCELLED" or "REFUNDED" or "MARKED_AS_REFUNDED");

    public async Task<IReadOnlyList<Payment>> RefreshAsync(CancellationToken ct)
    {
        var sent = await db.Payments.Where(p => p.Kind == "invoice" && p.Status == PaymentStatus.Sent && p.PayPalInvoiceId != null)
            .OrderByDescending(p => p.CreatedAtUtc).Take(50).ToListAsync(ct);
        foreach (var p in sent.Where(p => IsOpen(p.InvoiceState)))
        {
            try { p.RecordInvoiceState((await invoices.GetStatusAsync(p.PayPalInvoiceId!, ct)).Status, clock.UtcNow); }
            catch (ToolException) { /* keep the last known state; one flaky call must not hide the rest */ }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }
}

/// <summary>commerce.invoice_status: who has paid and who hasn't. Reads PayPal; changes nothing there.</summary>
public sealed class InvoiceStatusTool(InvoiceTracker tracker) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "commerce.invoice_status", 1,
        "Check which invoices you sent have been paid, are still open, or are overdue.",
        EffectClass.Read, WritesOnlyToStaging: true, [], ["text", "open", "paid"],
        TimeSpan.FromSeconds(60), MaxAttempts: 2, Idempotent: true, Integration: "paypal");

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var list = await tracker.RefreshAsync(ct);
        if (list.Count == 0) return new ToolResult(new JsonObject { ["text"] = "You haven't sent any invoices yet.", ["open"] = 0, ["paid"] = 0 });
        var lines = list.Select(p => $"- {p.Recipient}: ${Money.Format(p.Amount)} for {p.ProductName} · {Label(p.InvoiceState)}");
        var open = list.Count(p => InvoiceTracker.IsOpen(p.InvoiceState));
        var text = $"{list.Count} invoices: {list.Count - open} paid or closed, {open} open.\n" + string.Join("\n", lines);
        return new ToolResult(new JsonObject { ["text"] = text, ["open"] = open, ["paid"] = list.Count - open });
    }

    private static string Label(string? s) => s switch
    {
        "PAID" or "MARKED_AS_PAID" => "PAID",
        null => "sent",
        _ => s.ToLowerInvariant().Replace('_', ' '),
    };
}

/// <summary>commerce.invoice_remind: a polite reminder through PayPal, only after Final Review. Never nags or pesters paid invoices.</summary>
public sealed class InvoiceRemindTool(
    KasperDbContext db, IPayPalInvoices invoices, IClock clock, IOptionsMonitor<PaymentOptions> options) : ITool
{
    public static readonly TimeSpan MinGap = TimeSpan.FromHours(24);

    public ToolDescriptor Descriptor { get; } = new(
        "commerce.invoice_remind", 1,
        "Send a payment reminder for an unpaid invoice you sent (asks you first). Give the recipient's email.",
        EffectClass.ExternalWrite, WritesOnlyToStaging: false,
        [
            new ToolParameter("recipient", ParamType.Text, true, "Email of the person who owes you.", 320),
            new ToolParameter("note", ParamType.Text, false, "A short friendly message to include.", 500, Editable: true),
        ],
        ["invoiceId"], TimeSpan.FromMinutes(1), MaxAttempts: 1, Idempotent: false, Integration: "paypal");

    private static string? Email(JsonObject input)
    {
        var raw = Input.OptStr(input, "recipient")?.Trim();
        return raw is not null && MailAddress.TryCreate(raw, out var a) && a.Address == raw ? raw.ToLowerInvariant() : null;
    }

    private async Task<Payment?> FindAsync(string email, CancellationToken ct) =>
        await db.Payments.Where(p => p.Kind == "invoice" && p.Status == PaymentStatus.Sent && p.Recipient == email)
            .OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);

    public Task<JsonObject?> FingerprintExtrasAsync(JsonObject input, CancellationToken ct) => Extras(input, ct);

    private async Task<JsonObject?> Extras(JsonObject input, CancellationToken ct)
    {
        var email = Email(input);
        var p = email is null ? null : await FindAsync(email, ct);
        return new JsonObject
        {
            ["invoice"] = p?.PayPalInvoiceId ?? "none",
            ["amount"] = p is null ? "0" : Money.Format(p.Amount),
            ["recipient"] = email ?? "invalid",
        };
    }

    public async Task<IReadOnlyList<ScreenViolation>> ScreenAsync(JsonObject input, CancellationToken ct)
    {
        var email = Email(input);
        if (email is null) return [new("invalid_recipient", "That is not a valid email address.")];
        var p = await FindAsync(email, ct);
        if (p is null) return [new("no_open_invoice", $"You haven't sent an invoice to {email}.")];
        try { p.RecordInvoiceState((await invoices.GetStatusAsync(p.PayPalInvoiceId!, ct)).Status, clock.UtcNow); await db.SaveChangesAsync(ct); }
        catch (ToolException) { /* fall back to the last known state */ }
        if (!InvoiceTracker.IsOpen(p.InvoiceState)) return [new("already_paid", $"{email}'s invoice is already {p.InvoiceState!.ToLowerInvariant().Replace('_', ' ')}. No reminder needed.")];
        if (p.LastReminderAtUtc is { } last && clock.UtcNow - last < MinGap)
            return [new("reminder_too_soon", $"You already reminded {email} less than 24 hours ago.")];
        return [];
    }

    public async Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var email = Email(input) ?? throw new ToolException("invalid_recipient", "Invalid email.", ErrorCategory.Validation);
        var p = await FindAsync(email, ct) ?? throw new ToolException("no_open_invoice", "No invoice found.", ErrorCategory.Validation);
        var note = Input.OptStr(input, "note") ?? DefaultNote(p);
        return new ToolPreview($"Remind {email} about ${Money.Format(p.Amount)}", $"PayPal Invoicing › {email}",
            [new("To", email), new("Invoice", $"${Money.Format(p.Amount)} for {p.ProductName}"), new("Status", p.InvoiceState ?? "sent")],
            note, ["PayPal emails this reminder to the recipient only after you approve."]);
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var email = Email(input) ?? throw new ToolException("invalid_recipient", "Invalid email.", ErrorCategory.Validation);
        var p = await FindAsync(email, ct) ?? throw new ToolException("no_open_invoice", "No invoice found.", ErrorCategory.Validation);
        var live = await invoices.GetStatusAsync(p.PayPalInvoiceId!, ct);
        p.RecordInvoiceState(live.Status, clock.UtcNow);
        if (!InvoiceTracker.IsOpen(live.Status))
        {
            await db.SaveChangesAsync(CancellationToken.None);
            throw new ToolException("already_paid", "That invoice is already paid. No reminder sent.", ErrorCategory.Permanent);
        }
        if (p.LastReminderAtUtc is { } last && clock.UtcNow - last < MinGap)
            throw new ToolException("reminder_too_soon", "A reminder was already sent in the last 24 hours.", ErrorCategory.Permanent);
        await invoices.RemindAsync(p.PayPalInvoiceId!, Input.OptStr(input, "note") ?? DefaultNote(p), ct);
        p.RecordReminder(clock.UtcNow);
        await db.SaveChangesAsync(CancellationToken.None);
        return new ToolResult(new JsonObject { ["invoiceId"] = p.PayPalInvoiceId });
    }

    private static string DefaultNote(Payment p) =>
        $"Hi! A friendly reminder that your invoice for {p.ProductName} (${Money.Format(p.Amount)}) is still open. Thank you!";
}

/// <summary>
/// commerce.invoice_split: one request becomes one PayPal invoice per person. Code does the maths, so the shares always
/// add up to the total exactly (any spare cents go to the first person). One approval covers everyone listed.
/// </summary>
public sealed class InvoiceSplitTool(
    IPayPalInvoices invoices, KasperDbContext db, IClock clock, IOptionsMonitor<PaymentOptions> options) : ITool
{
    public const int MaxPeople = 10;

    public ToolDescriptor Descriptor { get; } = new(
        "commerce.invoice_split", 1,
        "Split an amount equally between several people by sending each one a PayPal invoice (asks you first). Use only when the user lists the emails.",
        EffectClass.ExternalWrite, WritesOnlyToStaging: false,
        [
            new ToolParameter("recipients", ParamType.Text, true, "Comma-separated emails of everyone who owes a share.", 700),
            new ToolParameter("total", ParamType.Text, true, "The total to split, in USD, digits only, e.g. 84 or 84.50.", 12),
            new ToolParameter("description", ParamType.Text, true, "What it is for, e.g. team dinner.", 200),
        ],
        ["count", "status"], TimeSpan.FromMinutes(5), MaxAttempts: 1, Idempotent: false, Integration: "paypal");

    private static List<string>? Emails(JsonObject input)
    {
        var parts = (Input.OptStr(input, "recipients") ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<string>();
        foreach (var raw in parts)
        {
            if (!MailAddress.TryCreate(raw, out var a) || a.Address != raw || !raw.Contains('.')) return null;
            var e = raw.ToLowerInvariant();
            if (!list.Contains(e)) list.Add(e);
        }
        return list.Count is >= 2 and <= MaxPeople ? list : null;
    }

    private static decimal? Total(JsonObject input) =>
        Money.TryParse(Input.OptStr(input, "total")?.Trim().TrimStart('$'), out var t) && t >= 2 && t <= InvoiceTool.MaxAmount ? decimal.Round(t, 2) : null;

    /// <summary>Equal shares in whole cents; spare cents go to the first people so the sum is exact.</summary>
    public static IReadOnlyList<decimal> Shares(decimal total, int people)
    {
        var cents = (long)decimal.Round(total * 100);
        var each = cents / people;
        var extra = (int)(cents % people);
        return Enumerable.Range(0, people).Select(i => (each + (i < extra ? 1 : 0)) / 100m).ToList();
    }

    public Task<JsonObject?> FingerprintExtrasAsync(JsonObject input, CancellationToken ct)
    {
        var emails = Emails(input);
        var total = Total(input);
        var shares = emails is null || total is null ? [] : Shares(total.Value, emails.Count);
        return Task.FromResult<JsonObject?>(new JsonObject
        {
            ["total"] = total is { } t ? Money.Format(t) : "invalid",
            ["currency"] = options.CurrentValue.Currency,
            ["shares"] = new JsonArray(emails is null ? [] : emails.Select((e, i) => (JsonNode?)(e + "=" + Money.Format(shares[i]))).ToArray()),
        });
    }

    public async Task<IReadOnlyList<ScreenViolation>> ScreenAsync(JsonObject input, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var v = new List<ScreenViolation>();
        var emails = Emails(input);
        var total = Total(input);
        if (emails is null) v.Add(new("invalid_recipients", $"List 2 to {MaxPeople} valid, different email addresses."));
        if (total is null) v.Add(new("invalid_amount", $"The total must be between 2.00 and {Money.Format(InvoiceTool.MaxAmount)} USD."));
        if (string.IsNullOrWhiteSpace(Input.OptStr(input, "description"))) v.Add(new("invalid_description", "The split needs a description."));
        if (emails is not null && total is not null)
        {
            if (Shares(total.Value, emails.Count).Min() < 1m) v.Add(new("share_too_small", "Each person's share must be at least $1.00."));
            var denied = o.AllowedRecipients.Count == 0 ? [] : emails.Where(e => !o.AllowedRecipients.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
            if (denied.Count > 0) v.Add(new("recipient_allowlist", $"{string.Join(", ", denied)} is not on your allowed recipients list."));
            var since = clock.UtcNow.AddMinutes(-o.DuplicateWindowMinutes);
            var shares = Shares(total.Value, emails.Count);
            var recent = await db.Payments.AsNoTracking().Where(p => p.Kind == "invoice" && p.CreatedAtUtc >= since
                && (p.Status == PaymentStatus.Approved || p.Status == PaymentStatus.Sent)).ToListAsync(ct);
            if (o.DuplicateWindowMinutes > 0 && emails.Select((e, i) => (e, i)).Any(x => recent.Any(p => p.Recipient == x.e && p.Amount == shares[x.i])))
                v.Add(new(Guardrails.DuplicatePurchase, "Someone on this list was already invoiced this exact amount a moment ago."));
        }
        if (v.Count > 0) await RecordBlockedAsync(v[0], input, ct);
        return v;
    }

    private async Task RecordBlockedAsync(ScreenViolation violation, JsonObject input, CancellationToken ct)
    {
        if (Emails(input) is not { } emails || Total(input) is not { } total) return;
        var blocked = Payment.ProposeInvoice(null, null, string.Join(", ", emails).Length > 300 ? emails[0] : string.Join(", ", emails),
            "Split: " + (Input.OptStr(input, "description") ?? ""), total, options.CurrentValue.Currency, "", clock.UtcNow);
        blocked.Block(violation.Rule, violation.Message, clock.UtcNow);
        db.Payments.Add(blocked);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var emails = Emails(input) ?? throw new ToolException("invalid_recipients", "Invalid recipients.", ErrorCategory.Validation);
        var total = Total(input) ?? throw new ToolException("invalid_amount", "Invalid total.", ErrorCategory.Validation);
        var shares = Shares(total, emails.Count);
        var table = string.Join("\n", emails.Select((e, i) => $"{e}  →  ${Money.Format(shares[i])}"));
        return new ToolPreview($"Split ${Money.Format(total)} between {emails.Count} people", "PayPal Invoicing › " + emails.Count + " invoices",
            [new("For", Input.Str(input, "description")), new("Total", $"{Money.Format(total)} USD"), new("People", emails.Count.ToString()), new("Sent with", "PayPal Invoicing (sandbox)")],
            table, [$"Nothing is sent yet. After you approve, each of the {emails.Count} people gets their own PayPal invoice."]);
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var emails = Emails(input) ?? throw new ToolException("invalid_recipients", "Invalid recipients.", ErrorCategory.Validation);
        var total = Total(input) ?? throw new ToolException("invalid_amount", "Invalid total.", ErrorCategory.Validation);
        var description = Input.Str(input, "description");
        var shares = Shares(total, emails.Count);
        var cur = options.CurrentValue.Currency;
        var fingerprint = ActionHasher.Compute(Descriptor.Id, Descriptor.Version, input, null, await FingerprintExtrasAsync(input, ct));
        var failures = new List<string>();

        for (var i = 0; i < emails.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var email = emails[i];
            var p = await db.Payments.FirstOrDefaultAsync(x => x.WorkflowId == context.WorkflowId && x.Kind == "invoice" && x.Recipient == email && x.Amount == shares[i], ct);
            if (p is null)
            {
                p = Payment.ProposeInvoice(context.WorkflowId, null, email, description, shares[i], cur, fingerprint, clock.UtcNow);
                db.Payments.Add(p);
            }
            if (p.Status == PaymentStatus.Sent) continue; // replay: never send twice
            try
            {
                if (p.Status == PaymentStatus.Proposed) p.TransitionTo(PaymentStatus.Approved, clock.UtcNow);
                await db.SaveChangesAsync(CancellationToken.None);
                if (p.PayPalInvoiceId is null)
                {
                    p.AttachInvoice((await invoices.CreateDraftAsync(email, description, shares[i], cur, ct)).Id);
                    await db.SaveChangesAsync(CancellationToken.None);
                }
                await invoices.SendAsync(p.PayPalInvoiceId!, ct);
                p.Sent(p.PayPalInvoiceId!, clock.UtcNow);
                p.RecordInvoiceState("SENT", clock.UtcNow);
                await db.SaveChangesAsync(CancellationToken.None);
                await ReceiptChain.SealAsync(db, p, CancellationToken.None);
            }
            catch (ToolException ex)
            {
                if (p.Status == PaymentStatus.Approved) p.Fail(ex.Message, clock.UtcNow);
                await db.SaveChangesAsync(CancellationToken.None);
                failures.Add($"{email}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
            throw new ToolException("split_partial", $"{emails.Count - failures.Count} of {emails.Count} invoices were sent. Problems: {string.Join("; ", failures)}", ErrorCategory.Permanent);
        return new ToolResult(new JsonObject { ["count"] = emails.Count, ["status"] = "Sent" });
    }
}
