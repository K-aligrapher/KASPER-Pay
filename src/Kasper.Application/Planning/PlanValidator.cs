using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kasper.Application.Tools;

namespace Kasper.Application.Planning;

/// <summary>
/// Server-side validation of model plans (FR-013, FR-021, FR-022). Rejects unknown tools,
/// invented parameters, forward/unknown references, unknown folders and unsafe file names.
/// </summary>
public static partial class PlanValidator
{
    public const int DefaultMaxSteps = 8;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly HashSet<string> AllowedTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".html", ".htm", ".xml", ".yaml", ".yml", ".log",
    };

    [GeneratedRegex("^s[0-9]{1,2}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^folder:[0-9]{1,6}$")]
    private static partial Regex FolderPattern();

    public static IReadOnlyList<string> Validate(Plan plan, IToolRegistry registry, ISet<string> allowedFolderRefs,
        int maxSteps = DefaultMaxSteps)
    {
        var errors = new List<string>();
        if (plan.Steps.Count == 0) errors.Add("A plan needs at least one step.");
        if (plan.Steps.Count > maxSteps) errors.Add($"Too many steps ({plan.Steps.Count}); the limit is {maxSteps}.");
        if (string.IsNullOrWhiteSpace(plan.Summary) || plan.Summary.Length > 300)
            errors.Add("summary must be 1–300 characters.");

        var earlier = new Dictionary<string, ToolDescriptor>();
        foreach (var step in plan.Steps)
        {
            var where = $"step '{step.Key}'";
            if (!KeyPattern().IsMatch(step.Key)) errors.Add($"{where}: id must look like s1, s2, …");
            else if (earlier.ContainsKey(step.Key)) errors.Add($"{where}: duplicate id.");

            if (registry.Find(step.ToolId) is not { } tool || registry.Available.All(t => t.Id != step.ToolId))
            {
                errors.Add($"{where}: unknown tool '{step.ToolId}'.");
                continue;
            }

            var d = tool.Descriptor;
            if (step.Purpose.Length > 300) errors.Add($"{where}: purpose is too long.");

            foreach (var (name, _) in step.Input)
                if (d.Parameters.All(p => p.Name != name))
                    errors.Add($"{where}: '{d.Id}' has no parameter '{name}'.");

            foreach (var p in d.Parameters)
            {
                step.Input.TryGetPropertyValue(p.Name, out var value);
                if (value is null)
                {
                    if (p.Required) errors.Add($"{where}: missing required '{p.Name}'.");
                    continue;
                }
                ValidateValue(where, p, value, earlier, allowedFolderRefs, errors);
            }

            if (KeyPattern().IsMatch(step.Key)) earlier.TryAdd(step.Key, d);
        }
        return errors;
    }

    /// <summary>Validates a single parameter value. Also used when the user edits an action.</summary>
    public static void ValidateValue(string where, ToolParameter p, JsonNode value,
        IReadOnlyDictionary<string, ToolDescriptor> earlier, ISet<string> allowedFolderRefs, List<string> errors)
    {
        if (StepReferences.LooksLikeRef(value))
        {
            if (!StepReferences.TryParse(value, out var key, out var field))
                errors.Add($"{where}: '{p.Name}' has a malformed reference.");
            else if (!earlier.TryGetValue(key, out var source))
                errors.Add($"{where}: '{p.Name}' refers to '{key}', which is not an earlier step.");
            else if (!source.Outputs.Contains(field))
                errors.Add($"{where}: '{key}' ({source.Id}) has no output '{field}'. It has: {string.Join(", ", source.Outputs)}.");
            else if (p.Type == ParamType.ArtifactRef && field != "artifact")
                errors.Add($"{where}: '{p.Name}' must reference an artifact output.");
            return;
        }

        if (p.Type == ParamType.ArtifactRef)
        {
            errors.Add($"{where}: '{p.Name}' must be a reference like \"$s1.artifact\".");
            return;
        }

        if (p.Type == ParamType.Integer)
        {
            if (value is not JsonValue iv || !iv.TryGetValue<int>(out _)) errors.Add($"{where}: '{p.Name}' must be a whole number.");
            return;
        }

        if (value is not JsonValue sv || !sv.TryGetValue<string>(out var s))
        {
            errors.Add($"{where}: '{p.Name}' must be text.");
            return;
        }

        if (s.Length > p.MaxLength) errors.Add($"{where}: '{p.Name}' is longer than {p.MaxLength} characters.");

        switch (p.Type)
        {
            case ParamType.FolderRef when !FolderPattern().IsMatch(s) || !allowedFolderRefs.Contains(s):
                errors.Add($"{where}: '{p.Name}' must be one of the allowed folders ({string.Join(", ", allowedFolderRefs)}).");
                break;
            case ParamType.FileName when FileNameProblem(s) is { } problem:
                errors.Add($"{where}: '{p.Name}' {problem}");
                break;
        }
    }

    /// <summary>Returns why a file name is unsafe, or null when it is fine.</summary>
    public static string? FileNameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120) return "must be 1–120 characters.";
        if (name != name.Trim() || name.EndsWith('.')) return "must not start/end with spaces or end with a dot.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') || name.Contains(':'))
            return "must be a plain file name without folders or special characters.";
        if (name.Contains(".."))
            return "must not contain '..'.";
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name)))
            return "uses a name Windows reserves.";
        if (!AllowedTextExtensions.Contains(Path.GetExtension(name)))
            return $"must end with one of: {string.Join(" ", AllowedTextExtensions)}.";
        return null;
    }
}
