using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kasper.Application.Commerce;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.PayPal;

/// <summary>
/// PayPal Orders v2 adapter. Only order ids and states are logged, never tokens, bodies or personal data.
/// Amounts are built from the catalog product, so the model can never influence them.
/// </summary>
public sealed class PayPalOrdersService(
    HttpClient http,
    PayPalTokenProvider tokens,
    IOptionsMonitor<PayPalOptions> options,
    ILogger<PayPalOrdersService> log) : IPayPalOrders
{
    private readonly PayPalHttp _api = new(http, tokens, options);

    private Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, string? requestId, CancellationToken ct) =>
        _api.SendAsync(method, path, body, requestId, ct);

    public async Task<PayPalOrder> CreateOrderAsync(string requestId, string referenceId, Product product, int quantity, CancellationToken ct)
    {
        if (quantity is < 1 or > 10) throw new ToolException("quantity_invalid", "Quantity must be 1–10.", ErrorCategory.Validation);

        var unit = Money.Format(product.Price);
        var total = Money.Format(product.Price * quantity);
        var body = new JsonObject
        {
            ["intent"] = "CAPTURE",
            ["purchase_units"] = new JsonArray(new JsonObject
            {
                ["reference_id"] = referenceId,
                ["description"] = Trim(product.Name, 127),
                ["amount"] = new JsonObject
                {
                    ["currency_code"] = product.Currency,
                    ["value"] = total,
                    ["breakdown"] = new JsonObject { ["item_total"] = new JsonObject { ["currency_code"] = product.Currency, ["value"] = total } },
                },
                ["items"] = new JsonArray(new JsonObject
                {
                    ["name"] = Trim(product.Name, 127),
                    ["sku"] = product.Id,
                    ["quantity"] = quantity.ToString(),
                    ["unit_amount"] = new JsonObject { ["currency_code"] = product.Currency, ["value"] = unit },
                }),
            }),
            ["application_context"] = new JsonObject
            {
                ["brand_name"] = "KASPER Pay",
                ["user_action"] = "PAY_NOW",
                ["shipping_preference"] = "NO_SHIPPING",
            },
        };

        var json = await SendAsync(HttpMethod.Post, "/v2/checkout/orders", body, requestId, ct);
        var order = ParseOrder(json);
        log.LogInformation("PayPal order {OrderId} created, status {Status}", order.Id, order.Status);
        return order;
    }

    public async Task<PayPalOrder> GetOrderAsync(string orderId, CancellationToken ct)
    {
        var order = ParseOrder(await SendAsync(HttpMethod.Get, $"/v2/checkout/orders/{Uri.EscapeDataString(orderId)}", null, null, ct));
        log.LogInformation("PayPal order {OrderId} status {Status}", order.Id, order.Status);
        return order;
    }

    public async Task<PayPalCapture> CaptureAsync(string orderId, string requestId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, $"/v2/checkout/orders/{Uri.EscapeDataString(orderId)}/capture", [], requestId, ct);
        var capture = json["purchase_units"]?[0]?["payments"]?["captures"]?[0] as JsonObject;
        var amount = capture?["amount"] as JsonObject;
        var result = new PayPalCapture(
            json["id"]?.GetValue<string>() ?? orderId,
            capture?["status"]?.GetValue<string>() ?? json["status"]?.GetValue<string>() ?? "UNKNOWN",
            capture?["id"]?.GetValue<string>(),
            amount?["currency_code"]?.GetValue<string>(),
            Money.TryParse(amount?["value"]?.GetValue<string>(), out var v) ? v : null);
        log.LogInformation("PayPal order {OrderId} capture status {Status}", orderId, result.Status);
        return result;
    }

    internal static PayPalOrder ParseOrder(JsonObject json)
    {
        var id = json["id"]?.GetValue<string>() ?? throw new ToolException("paypal_bad_response", "PayPal returned no order id.", ErrorCategory.Permanent);
        var links = json["links"] as JsonArray ?? [];
        var approve = links.OfType<JsonObject>()
            .FirstOrDefault(l => l["rel"]?.GetValue<string>() is "payer-action" or "approve")?["href"]?.GetValue<string>();
        var amount = json["purchase_units"]?[0]?["amount"] as JsonObject;
        return new PayPalOrder(id, json["status"]?.GetValue<string>() ?? "UNKNOWN", approve,
            amount?["currency_code"]?.GetValue<string>(),
            Money.TryParse(amount?["value"]?.GetValue<string>(), out var v) ? v : null);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
