using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Files;

namespace Kasper.Infrastructure.Tools;

/// <summary>
/// files.open: finds a file by name in your allowed folders and copies it into the task's staging,
/// so chat requests like "make slides from report.docx" can work on existing files. Read-only: the original is never changed.
/// </summary>
public sealed partial class OpenFileTool(ArtifactStore artifacts, FileScope scope) : DocumentToolBase(artifacts), ITool
{
    private const long MaxBytes = 200L * 1024 * 1024;

    public ToolDescriptor Descriptor { get; } = new(
        "files.open", 1, "Open an existing file from your allowed folders by its name (searches subfolders too). Use its artifact with other tools.",
        EffectClass.Read, true,
        [
            new ToolParameter("name", ParamType.Text, true, "The file name as the user wrote it, e.g. report.docx.", 260),
            new ToolParameter("folder", ParamType.FolderRef, false, "Only search this allowed folder."),
        ],
        ["artifact", "fileName", "sizeBytes", "path"], TimeSpan.FromSeconds(60), MaxAttempts: 1, Idempotent: true);

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    /// <summary>"EWaste_Seminar____20(1).docx" and "ewaste seminar 20 (1).docx" compare equal.</summary>
    internal static string Loose(string name) => NonAlnum().Replace(name.ToLowerInvariant(), "");

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var name = Input.Str(input, "name").Trim().Trim('"', '\'');
        if (name.IndexOfAny(['\\', '/', ':']) >= 0 || name.Contains(".."))
            throw new ToolException("name_invalid", "Give just the file name, not a path.", ErrorCategory.Validation);

        var folders = Input.OptStr(input, "folder") is { } r
            ? [(await scope.ResolveRefAsync(r, ct)).Path]
            : (await scope.ListAsync(ct)).Select(f => f.Path).ToList();

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5 };
        var all = folders.Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "*", options))
            .Where(p => !p.Contains(@"\node_modules\") && !p.Contains(@"\.git\"))
            .Take(50_000).ToList();

        var exact = all.Where(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact : all.Where(p => Loose(Path.GetFileName(p)) == Loose(name)).ToList();
        if (matches.Count == 0)
        {
            // Name without the extension, e.g. "the seminar doc"
            var stem = Loose(Path.GetFileNameWithoutExtension(name));
            if (stem.Length >= 4) matches = all.Where(p => Loose(Path.GetFileNameWithoutExtension(p)).Contains(stem)).ToList();
        }

        if (matches.Count == 0)
            throw new ToolException("file_not_found",
                $"Couldn't find “{name}” in your allowed folders ({string.Join(", ", folders)}). Add its folder in Settings, or drop the file on Documents.",
                ErrorCategory.Validation);
        var file = matches.OrderByDescending(File.GetLastWriteTimeUtc).First(); // newest if several
        if (new FileInfo(file).Length > MaxBytes)
            throw new ToolException("file_too_large", $"{Path.GetFileName(file)} is larger than 200 MB.", ErrorCategory.Validation);

        // Copy first so the original stays untouched and can't change under us.
        var copy = Path.Combine(ArtifactStore.WorkDir(context.WorkflowId), $"open-{context.StepId:N}{Path.GetExtension(file)}");
        File.Copy(file, copy, overwrite: true);
        return await StageAsync(context, copy, Path.GetFileName(file), ct, new JsonObject { ["path"] = file });
    }
}
