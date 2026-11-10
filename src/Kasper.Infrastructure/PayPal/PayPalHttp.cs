using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kasper.Application.Commerce;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.PayPal;

/// <summary>Authenticated PayPal REST calls shared by Orders and Invoicing. Errors carry only the issue name.</summary>
public sealed class PayPalHttp(HttpClient http, PayPalTokenProvider tokens, IOptionsMonitor<PayPalOptions> options)
{
    public async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, string? requestId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(method, options.CurrentValue.BaseUrl + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(ct));
            if (requestId is not null) req.Headers.TryAddWithoutValidation("PayPal-Request-Id", requestId);
            if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            HttpResponseMessage res;
            try { res = await http.SendAsync(req, ct); }
            catch (HttpRequestException ex) { throw new ToolException("paypal_unreachable", "Can't reach PayPal.", ErrorCategory.Transient, ex); }

            using (res)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                if ((int)res.StatusCode == 401 && attempt == 0) { tokens.Invalidate(); continue; }
                if (res.IsSuccessStatusCode) return string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject ?? [];
                throw Classify((int)res.StatusCode, text);
            }
        }
        throw new ToolException("paypal_unauthorized", "PayPal rejected the credentials.", ErrorCategory.Authorization);
    }

    private static ToolException Classify(int status, string body)
    {
        string? issue = null;
        try
        {
            var j = JsonNode.Parse(body) as JsonObject;
            issue = j?["details"]?[0]?["issue"]?.GetValue<string>() ?? j?["name"]?.GetValue<string>();
        }
        catch (System.Text.Json.JsonException) { }

        return status switch
        {
            401 or 403 => new ToolException("paypal_unauthorized", "PayPal rejected the request. Check the sandbox credentials and app permissions.", ErrorCategory.Authorization),
            429 => new ToolException("paypal_rate_limited", "PayPal is rate limiting; KASPER will retry.", ErrorCategory.RateLimit),
            >= 500 => new ToolException("paypal_error", "PayPal had a temporary problem.", ErrorCategory.Transient),
            _ => new ToolException("paypal_" + (issue ?? status.ToString()).ToLowerInvariant(),
                $"PayPal refused the request ({issue ?? status.ToString()}).", ErrorCategory.Permanent),
        };
    }
}
