using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Application.Planning;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Files;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kasper.Infrastructure.Tools;

internal static class Input
{
    public static string Str(JsonObject input, string name) =>
        input[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s
            : throw new ToolException("input_invalid", $"'{name}' is missing.", ErrorCategory.Validation);

    public static string? OptStr(JsonObject input, string name) =>
        input[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    public static int Int(JsonObject input, string name, int fallback) =>
        input[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;
}

/// <summary>ai.generate_text: writes text with the local model. No side effects.</summary>
public sealed class GenerateTextTool(IChatModel model) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "ai.generate_text", 1, "Write text with AI (notes, summaries, drafts, lists).",
        EffectClass.Read, WritesOnlyToStaging: true,
        [new ToolParameter("instructions", ParamType.Text, true, "What to write, with all needed details.", 4000)],
        ["text"], TimeSpan.FromMinutes(5), MaxAttempts: 2, Idempotent: true);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        try
        {
            var reply = await model.ChatAsync(
            [
                new ChatMessageDto("system", "You write content for the user. Output only the requested content, with no preamble or closing remarks."),
                new ChatMessageDto("user", Input.Str(input, "instructions")),
            ], null, ct);
            return new ToolResult(new JsonObject { ["text"] = reply.Content });
        }
        catch (ChatModelException ex)
        {
            throw new ToolException(ex.Code, ex.Message, ex.Retryable ? ErrorCategory.Transient : ErrorCategory.Permanent, ex);
        }
    }
}

/// <summary>files.write_text: prepares a text file in KASPER's private staging area.</summary>
public sealed class WriteTextFileTool(ArtifactStore artifacts, KasperDbContext db) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "files.write_text", 1, "Prepare a text file (.md, .txt, .csv, .json, .html…) in staging. Nothing is saved yet.",
        EffectClass.LocalWrite, WritesOnlyToStaging: true,
        [
            new ToolParameter("fileName", ParamType.FileName, true, "File name with extension, e.g. notes.md", 120),
            new ToolParameter("content", ParamType.Text, true, "The file content.", 200_000),
        ],
        ["artifact", "fileName", "sizeBytes"], TimeSpan.FromSeconds(30), MaxAttempts: 2, Idempotent: true);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var fileName = Input.Str(input, "fileName");
        if (PlanValidator.FileNameProblem(fileName) is { } problem)
            throw new ToolException("file_name_invalid", $"File name {problem}", ErrorCategory.Validation);
        var content = Input.Str(input, "content");

        // Replay after restart: reuse the artifact this step already made if the content is the same.
        var hash = ArtifactStore.Sha256Hex(new System.Text.UTF8Encoding(false).GetBytes(content));
        var artifact = await db.Artifacts.FirstOrDefaultAsync(a => a.StepId == context.StepId && a.Sha256 == hash && a.FileName == fileName, ct)
            is { } existing && File.Exists(existing.StagingPath)
            ? existing
            : await artifacts.CreateTextAsync(context.WorkflowId, context.StepId, fileName, content, ct);

        return new ToolResult(new JsonObject
        {
            ["artifact"] = artifact.Id.ToString(),
            ["fileName"] = artifact.FileName,
            ["sizeBytes"] = artifact.SizeBytes,
        });
    }
}

/// <summary>
/// files.commit: saves a staged file into an allowed folder. Never overwrites: if the name is
/// taken, it saves "name (2).ext". Requires Final Review.
/// </summary>
public sealed class CommitFileTool(ArtifactStore artifacts, FileScope scope, IClock clock, KasperDbContext db) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "files.commit", 1, "Save a prepared file into one of the allowed folders (asks the user first).",
        EffectClass.LocalWrite, WritesOnlyToStaging: false,
        [
            new ToolParameter("artifact", ParamType.ArtifactRef, true, "The prepared file, e.g. \"$s1.artifact\"."),
            new ToolParameter("folder", ParamType.FolderRef, true, "Allowed folder, e.g. folder:1.", 20, Editable: true),
            new ToolParameter("fileName", ParamType.FileName, false, "Optional new file name.", 120, Editable: true),
        ],
        ["path"], TimeSpan.FromSeconds(60), MaxAttempts: 2, Idempotent: true);

    public async Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var (artifact, folder, name, target) = await ResolveAsync(input, ct);
        var warnings = new List<string>();
        if (File.Exists(target))
            warnings.Add($"A file named {name} already exists there. KASPER will save a copy with a number added; nothing is overwritten.");

        string? content = null;
        if (artifact.MimeType.StartsWith("text/") || artifact.MimeType is "application/json" or "application/xml" or "application/yaml")
        {
            var text = await File.ReadAllTextAsync(artifact.StagingPath, ct);
            content = text.Length > 8000 ? text[..8000] + "\n…" : text;
        }

        return new ToolPreview($"Save {name}", target,
        [
            new PreviewField("File", name),
            new PreviewField("Folder", folder),
            new PreviewField("Size", FormatSize(artifact.SizeBytes)),
        ], content, warnings);
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var (artifact, folder, name, target) = await ResolveAsync(input, ct);

        // Already committed (replay after a restart): reconcile instead of saving twice.
        if (artifact.CommittedPath is { } done && await ArtifactStore.CurrentHashAsync(done, ct) == artifact.Sha256)
            return Result(done);

        if (await ArtifactStore.CurrentHashAsync(artifact.StagingPath, ct) is not { } stagedHash)
            throw new ToolException("artifact_missing", "The prepared file is gone from staging.", ErrorCategory.Permanent);
        if (stagedHash != artifact.Sha256)
            throw new ToolException("artifact_changed", "The prepared file changed after it was reviewed.", ErrorCategory.Validation);

        var final = target;
        for (var n = 2; File.Exists(final); n++)
        {
            if (await ArtifactStore.CurrentHashAsync(final, ct) == artifact.Sha256)
            {
                // Same content already there (earlier attempt finished): reconcile.
                artifact.MarkCommitted(final, clock.UtcNow);
                await db.SaveChangesAsync(ct);
                return Result(final);
            }
            final = FileScope.SafeCombine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
        }

        // Atomic: copy to a temp name in the destination folder, then rename without overwrite.
        var tmp = Path.Combine(folder, $".kasper-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(artifact.StagingPath, tmp);
            File.Move(tmp, final, overwrite: false);
        }
        catch (IOException ex)
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw new ToolException("save_failed", $"Could not save the file: {ex.Message}", ErrorCategory.Transient, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw new ToolException("save_denied", "Windows denied access to that folder.", ErrorCategory.Authorization, ex);
        }

        artifact.MarkCommitted(final, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return Result(final);
    }

    private static ToolResult Result(string path) => new(new JsonObject { ["path"] = path });

    private async Task<(Domain.Entities.Artifact Artifact, string Folder, string Name, string Target)> ResolveAsync(JsonObject input, CancellationToken ct)
    {
        if (!Guid.TryParse(Input.Str(input, "artifact"), out var id) || await artifacts.FindAsync(id, ct) is not { } artifact)
            throw new ToolException("artifact_unknown", "The prepared file was not found.", ErrorCategory.Permanent);
        var folder = (await scope.ResolveRefAsync(Input.Str(input, "folder"), ct)).Path;
        var name = Input.OptStr(input, "fileName") ?? artifact.FileName;
        return (artifact, folder, name, FileScope.SafeCombine(folder, name));
    }

    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} bytes" : $"{bytes / 1024.0:0.#} KB";
}
