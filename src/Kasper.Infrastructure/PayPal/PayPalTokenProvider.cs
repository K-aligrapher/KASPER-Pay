using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Commerce;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.PayPal;

/// <summary>
/// Client-credentials OAuth. The token is cached in memory only and refreshed 60 s before it
/// expires. The token and secret are never logged and never leave this class except in the
/// Authorization header of PayPal requests.
/// </summary>
public sealed class PayPalTokenProvider(HttpClient http, IOptionsMonitor<PayPalOptions> options, IClock clock)
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTime _expiresUtc;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (Valid()) return _token!;
        await _gate.WaitAsync(ct);
        try
        {
            if (Valid()) return _token!;
            var o = options.CurrentValue;
            if (!o.IsConfigured)
                throw new ToolException("paypal_not_configured",
                    "PayPal is not configured. Set PayPal:ClientId and PayPal:ClientSecret in user secrets.", ErrorCategory.Authorization);

            using var req = new HttpRequestMessage(HttpMethod.Post, o.BaseUrl + "/v1/oauth2/token")
            {
                Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.ClientId}:{o.ClientSecret}")));

            HttpResponseMessage res;
            try { res = await http.SendAsync(req, ct); }
            catch (HttpRequestException ex) { throw new ToolException("paypal_unreachable", "Can't reach PayPal.", ErrorCategory.Transient, ex); }
            using (res)
            {
                if ((int)res.StatusCode is 401 or 403)
                    throw new ToolException("paypal_unauthorized", "PayPal rejected the Client ID/Secret. Check the sandbox credentials.", ErrorCategory.Authorization);
                if (!res.IsSuccessStatusCode)
                    throw new ToolException("paypal_token_failed", $"PayPal token request failed ({(int)res.StatusCode}).",
                        (int)res.StatusCode >= 500 ? ErrorCategory.Transient : ErrorCategory.Permanent);

                var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject;
                _token = json?["access_token"]?.GetValue<string>()
                    ?? throw new ToolException("paypal_token_failed", "PayPal returned no access token.", ErrorCategory.Permanent);
                var seconds = json["expires_in"]?.GetValue<int>() ?? 300;
                _expiresUtc = clock.UtcNow.AddSeconds(seconds);
                return _token;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Forget the cached token (e.g. after a 401 from an API call).</summary>
    public void Invalidate() => _token = null;

    private bool Valid() => _token is not null && clock.UtcNow < _expiresUtc - Skew;
}
