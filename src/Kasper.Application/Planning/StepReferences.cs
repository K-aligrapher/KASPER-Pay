using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kasper.Application.Planning;

/// <summary>
/// Steps refer to earlier outputs with a string value of the form "$s1.text".
/// References are resolved by the server; the model never handles real paths or IDs.
/// </summary>
public static partial class StepReferences
{
    [GeneratedRegex(@"^\$(?<key>s[0-9]{1,2})\.(?<field>[a-zA-Z][a-zA-Z0-9_]{0,40})$")]
    private static partial Regex RefPattern();

    public static bool TryParse(JsonNode? node, out string key, out string field)
    {
        key = field = "";
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s)) return false;
        var m = RefPattern().Match(s);
        if (!m.Success) return false;
        key = m.Groups["key"].Value;
        field = m.Groups["field"].Value;
        return true;
    }

    public static bool LooksLikeRef(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('$');

    public static IEnumerable<string> ReferencedKeys(JsonObject input)
    {
        foreach (var (_, value) in input)
            if (TryParse(value, out var key, out _)) yield return key;
    }

    /// <summary>
    /// Replaces references with values from completed step outputs. Returns null when any
    /// referenced step has not produced output yet.
    /// </summary>
    public static JsonObject? Resolve(JsonObject input, Func<string, JsonObject?> outputsByKey)
    {
        var resolved = new JsonObject();
        foreach (var (name, value) in input)
        {
            if (TryParse(value, out var key, out var field))
            {
                var output = outputsByKey(key);
                if (output is null || !output.TryGetPropertyValue(field, out var produced)) return null;
                resolved[name] = produced?.DeepClone();
            }
            else
            {
                resolved[name] = value?.DeepClone();
            }
        }
        return resolved;
    }
}
