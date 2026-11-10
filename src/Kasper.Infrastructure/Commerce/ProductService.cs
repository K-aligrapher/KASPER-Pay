using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Commerce;
using Kasper.Application.Tools;
using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Commerce;

/// <summary>Channel3 product search (live retail products). Only the key and the query leave the machine.</summary>
public sealed class Channel3Client(HttpClient http, IOptionsMonitor<Channel3Options> options)
{
    public sealed record Hit(string Id, string Title, string? Brand, string Domain, string? Url, string? ImageUrl, decimal Price, string Currency);

    public async Task<IReadOnlyList<Hit>> SearchAsync(string query, decimal? maxPrice, int limit, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var filters = new JsonObject();
        if (maxPrice is { } max) filters["price"] = new JsonObject { ["max_price"] = (double)max };
        var body = new JsonObject
        {
            ["query"] = query,
            ["limit"] = Math.Clamp(limit, 1, 30),
            ["filters"] = filters,
            ["config"] = new JsonObject { ["country"] = "US", ["currency"] = "USD" },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, o.BaseUrl.TrimEnd('/') + "/v1/search") { Content = JsonContent.Create(body) };
        req.Headers.TryAddWithoutValidation("x-api-key", o.ApiKey);

        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new ToolException("channel3_unreachable", "Can't reach Channel3 product search.", ErrorCategory.Transient, ex); }
        using (res)
        {
            if ((int)res.StatusCode is 401 or 403)
                throw new ToolException("channel3_unauthorized", "Channel3 rejected the API key.", ErrorCategory.Authorization);
            if ((int)res.StatusCode == 402)
                throw new ToolException("channel3_quota", "Channel3 free search credits are used up.", ErrorCategory.Permanent);
            if ((int)res.StatusCode == 429 || (int)res.StatusCode >= 500)
                throw new ToolException("channel3_error", "Channel3 had a temporary problem.", ErrorCategory.Transient);
            if (!res.IsSuccessStatusCode)
                throw new ToolException("channel3_" + (int)res.StatusCode, "Channel3 refused the search.", ErrorCategory.Permanent);

            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject ?? [];
            return Parse(json);
        }
    }

    internal static IReadOnlyList<Hit> Parse(JsonObject json)
    {
        var hits = new List<Hit>();
        foreach (var p in (json["products"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var offer = (p["offers"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(o => o["availability"]?.GetValue<string>() != "OutOfStock" && o["price"]?["price"] is not null
                    && string.Equals(o["price"]?["currency"]?.GetValue<string>() ?? "USD", "USD", StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => o["price"]!["price"]!.GetValue<double>()).FirstOrDefault();
            var id = p["id"]?.GetValue<string>();
            var title = p["title"]?.GetValue<string>();
            if (offer is null || id is null || string.IsNullOrWhiteSpace(title)) continue;

            var images = (p["images"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var image = (images.FirstOrDefault(i => i["is_main_image"]?.GetValue<bool>() == true) ?? images.FirstOrDefault())?["url"]?.GetValue<string>();
            hits.Add(new Hit(id, title.Trim(), (p["brands"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["name"]?.GetValue<string>(),
                offer["domain"]?.GetValue<string>() ?? "online retailer", offer["url"]?.GetValue<string>(), image,
                decimal.Round((decimal)offer["price"]!["price"]!.GetValue<double>(), 2), "USD"));
        }
        return hits;
    }
}

/// <summary>
/// Finds products (Channel3 when a key is set, otherwise the seeded catalog) and freezes the result as a
/// quote. Resolves ids back to products for the purchase tool: seeded ids and quote ids.
/// </summary>
public sealed class ProductService(
    KasperDbContext db, Channel3Client channel3, ICatalog seed, IOptionsMonitor<Channel3Options> options, IClock clock)
    : IProductResolver, IProductFinder
{
    public static readonly TimeSpan QuoteLifetime = TimeSpan.FromHours(6);

    public async Task<Product?> ResolveAsync(string productId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(productId)) return null;
        if (!productId.StartsWith("q_", StringComparison.Ordinal)) return seed.Find(productId);
        var q = await db.Quotes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == productId, ct);
        if (q is null || clock.UtcNow - q.CreatedAtUtc > QuoteLifetime) return null; // stale quotes are refused
        return new Product(q.Id, q.Brand is { Length: > 0 } b && !q.Title.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? $"{b} {q.Title}" : q.Title,
            "", q.Price, q.Merchant, q.Currency, q.ImageUrl, q.Url);
    }

    public async Task<IReadOnlyList<Product>> FindAsync(string query, decimal? maxPrice, int take, CancellationToken ct)
    {
        var found = new List<ProductQuote>();
        if (options.CurrentValue.IsConfigured)
        {
            foreach (var h in await channel3.SearchAsync(query, maxPrice, Math.Max(take * 3, 10), ct))
            {
                if (maxPrice is { } max && h.Price > max) continue; // code re-checks the budget, never trusts the filter alone
                found.Add(ProductQuote.Create("channel3", h.Id, h.Title, h.Brand, h.Domain, h.Url, h.ImageUrl, h.Price, h.Currency, clock.UtcNow));
                if (found.Count == take) break;
            }
        }
        else
        {
            foreach (var p in SeedMatches(query, maxPrice).Take(take))
                found.Add(ProductQuote.Create("seed", p.Id, p.Name, null, p.Merchant, null, null, p.Price, p.Currency, clock.UtcNow));
        }

        db.Quotes.AddRange(found);
        await db.SaveChangesAsync(ct);
        return found.Select(q => new Product(q.Id, q.Title, "", q.Price, q.Merchant, q.Currency, q.ImageUrl, q.Url)).ToList();
    }

    private IEnumerable<Product> SeedMatches(string query, decimal? maxPrice)
    {
        var words = query.ToLowerInvariant().Split([' ', ',', '-'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToArray();
        return seed.All.Where(p => maxPrice is null || p.Price <= maxPrice)
            .Select(p => (p, score: words.Count(w => (p.Name + " " + p.Description).Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.score > 0).OrderByDescending(x => x.score).ThenBy(x => x.p.Price).Select(x => x.p);
    }
}

/// <summary>commerce.find_product: code searches and freezes a quote. The model only supplies words and a budget.</summary>
public sealed class FindProductTool(IProductFinder finder) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "commerce.find_product", 1,
        "Search for a product to buy. Always do this BEFORE commerce.purchase, then pass \"$s1.productId\" on. Use the user's words and their budget.",
        EffectClass.Read, WritesOnlyToStaging: true,
        [
            new ToolParameter("query", ParamType.Text, true, "What to look for, e.g. wireless mouse.", 200),
            new ToolParameter("maxPrice", ParamType.Text, false, "The user's budget in USD, digits only, e.g. 100. Leave out if none.", 12),
        ],
        ["productId", "name", "price", "merchant", "text"], TimeSpan.FromSeconds(30), MaxAttempts: 2, Idempotent: true, Integration: "paypal");

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var query = Tools.Input.Str(input, "query").Trim();
        decimal? max = Money.TryParse(Tools.Input.OptStr(input, "maxPrice")?.Trim().TrimStart('$'), out var m) && m > 0 ? m : null;
        var hits = await finder.FindAsync(query, max, 3, ct);
        if (hits.Count == 0)
            throw new ToolException("no_product", max is null ? "I couldn't find a matching product." : $"I couldn't find a matching product under ${Money.Format(max.Value)}.", ErrorCategory.Validation);
        var best = hits[0];
        var text = "Best match first (prices are from the store, not the AI):\n" +
                   string.Join("\n", hits.Select(h => $"- {h.Name} · ${Money.Format(h.Price)} · {h.Merchant}"));
        return new ToolResult(new JsonObject
        {
            ["productId"] = best.Id, ["name"] = best.Name, ["price"] = Money.Format(best.Price), ["merchant"] = best.Merchant, ["text"] = text,
        });
    }
}
