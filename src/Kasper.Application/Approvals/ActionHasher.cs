using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kasper.Application.Approvals;

/// <summary>
/// Canonical hash of an action: tool, version, fully resolved input and the content hashes of
/// any files it touches. Any material change gives a different hash, which invalidates approval.
/// </summary>
public static class ActionHasher
{
    public static string Compute(string toolId, int toolVersion, JsonObject resolvedInput,
        IReadOnlyDictionary<string, string>? artifactHashes = null, JsonObject? extras = null)
    {
        var doc = new JsonObject
        {
            ["tool"] = toolId,
            ["version"] = toolVersion,
            ["input"] = Canonicalize(resolvedInput),
            ["artifacts"] = Canonicalize(new JsonObject(
                (artifactHashes ?? new Dictionary<string, string>())
                .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))))
        };
        if (extras is not null) doc["extras"] = Canonicalize(extras);
        var bytes = Encoding.UTF8.GetBytes(doc.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Recursively sorts object keys so equal content always serializes the same way.</summary>
    public static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Canonicalize(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonicalize).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };
}
