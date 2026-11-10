using System.Text.Json.Nodes;
using Kasper.Application.Commerce;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.PayPal;

/// <summary>PayPal Invoicing v2: create a draft, then send it. Sending happens only after Final Review.</summary>
public sealed class PayPalInvoicingService(
    HttpClient http, PayPalTokenProvider tokens, IOptionsMonitor<PayPalOptions> options,
    ILogger<PayPalInvoicingService> log) : IPayPalInvoices
{
    private readonly PayPalHttp _api = new(http, tokens, options);

    public async Task<PayPalInvoiceDraft> CreateDraftAsync(string email, string description, decimal amount, string currency, CancellationToken ct)
    {
        var number = (await _api.SendAsync(HttpMethod.Post, "/v2/invoicing/generate-next-invoice-number", [], null, ct))["invoice_number"]?.GetValue<string>();
        var body = new JsonObject
        {
            ["detail"] = new JsonObject { ["invoice_number"] = number, ["currency_code"] = currency, ["note"] = "Sent with KASPER Pay" },
            ["primary_recipients"] = new JsonArray(new JsonObject { ["billing_info"] = new JsonObject { ["email_address"] = email } }),
            ["items"] = new JsonArray(new JsonObject
            {
                ["name"] = description.Length <= 200 ? description : description[..200],
                ["quantity"] = "1",
                ["unit_amount"] = new JsonObject { ["currency_code"] = currency, ["value"] = Money.Format(amount) },
            }),
        };
        var json = await _api.SendAsync(HttpMethod.Post, "/v2/invoicing/invoices", body, null, ct);
        var href = json["href"]?.GetValue<string>();
        var id = href?.TrimEnd('/').Split('/').Last() ?? json["id"]?.GetValue<string>()
            ?? throw new ToolException("paypal_bad_response", "PayPal returned no invoice id.", ErrorCategory.Permanent);
        log.LogInformation("PayPal invoice draft {InvoiceId} created", id);
        return new PayPalInvoiceDraft(id, href);
    }

    public async Task<PayPalInvoiceStatus> GetStatusAsync(string invoiceId, CancellationToken ct)
    {
        var json = await _api.SendAsync(HttpMethod.Get, $"/v2/invoicing/invoices/{Uri.EscapeDataString(invoiceId)}", null, null, ct);
        var due = json["due_amount"]?["value"]?.GetValue<string>();
        return new PayPalInvoiceStatus(json["status"]?.GetValue<string>() ?? "UNKNOWN", Money.TryParse(due, out var d) ? d : null);
    }

    public async Task RemindAsync(string invoiceId, string note, CancellationToken ct)
    {
        await _api.SendAsync(HttpMethod.Post, $"/v2/invoicing/invoices/{Uri.EscapeDataString(invoiceId)}/remind",
            new JsonObject { ["subject"] = "Payment reminder", ["note"] = note, ["send_to_invoicer"] = false }, null, ct);
        log.LogInformation("PayPal invoice {InvoiceId} reminder sent", invoiceId);
    }

    public async Task SendAsync(string invoiceId, CancellationToken ct)
    {
        await _api.SendAsync(HttpMethod.Post, $"/v2/invoicing/invoices/{Uri.EscapeDataString(invoiceId)}/send",
            new JsonObject { ["send_to_recipient"] = true, ["send_to_invoicer"] = false }, null, ct);
        log.LogInformation("PayPal invoice {InvoiceId} sent", invoiceId);
    }
}
