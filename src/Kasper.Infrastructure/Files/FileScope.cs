using Kasper.Application.Planning;
using Kasper.Application.Tools;
using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kasper.Infrastructure.Files;

/// <summary>
/// The folders KASPER may save into (FR-005, NFR-006). Paths are canonicalized, must stay inside
/// an allowed folder, and may not pass through junctions/symlinks that could escape it.
/// </summary>
public sealed class FileScope(KasperDbContext db, Integrations.SlackSettingsStore slack)
{
    /// <summary>
    /// Everything a plan may reference (FR-005): "folder:N", "#channel" for allowlisted Slack
    /// channels, and "repo:owner/name" for GitHub repos linked to a project.
    /// </summary>
    public async Task<HashSet<string>> AllowedRefsAsync(CancellationToken ct)
    {
        var refs = (await ListAsync(ct)).Select(RefFor).ToHashSet();
        if (slack.Load() is { } s)
            foreach (var c in s.Channels) refs.Add("#" + c.Name.ToLowerInvariant());
        foreach (var repo in await db.Projects.Where(p => p.GitHubRepo != null).Select(p => p.GitHubRepo!).ToListAsync(ct))
            refs.Add("repo:" + repo.ToLowerInvariant());
        return refs;
    }

    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("KASPER_DEFAULT_SAVE_FOLDER") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KASPER"));

    public static string RefFor(AllowedFolder f) => $"folder:{f.Id}";

    /// <summary>Makes sure the default "Documents\KASPER" folder exists and is allowed.</summary>
    /// <remarks>Safe to call concurrently (worker startup and the first request race on a fresh database).</remarks>
    public async Task EnsureDefaultAsync(DateTime nowUtc, CancellationToken ct)
    {
        Directory.CreateDirectory(DefaultFolder);
        var full = Normalize(DefaultFolder);
        await EnsureLock.WaitAsync(ct);
        try
        {
            if (await db.AllowedFolders.AnyAsync(f => f.Path == full, ct)) return;
            var folder = new AllowedFolder { Path = full, AddedAtUtc = nowUtc, IsDefault = true };
            db.AllowedFolders.Add(folder);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another process added it first; that's fine.
                db.Entry(folder).State = EntityState.Detached;
            }
        }
        finally
        {
            EnsureLock.Release();
        }
    }

    private static readonly SemaphoreSlim EnsureLock = new(1, 1);

    public Task<List<AllowedFolder>> ListAsync(CancellationToken ct) =>
        db.AllowedFolders.OrderBy(f => f.Id).ToListAsync(ct);

    public async Task<AllowedFolder> ResolveRefAsync(string folderRef, CancellationToken ct)
    {
        if (!folderRef.StartsWith("folder:", StringComparison.Ordinal) || !int.TryParse(folderRef[7..], out var id))
            throw new ToolException("folder_invalid", $"'{folderRef}' is not a folder reference.", ErrorCategory.Validation);
        var folder = await db.AllowedFolders.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new ToolException("folder_not_allowed", $"{folderRef} is not an allowed folder.", ErrorCategory.Validation);
        if (!Directory.Exists(folder.Path))
            throw new ToolException("folder_missing", $"The folder {folder.Path} no longer exists.", ErrorCategory.Permanent);
        return folder;
    }

    /// <summary>Combines a folder and file name and proves the result stays inside the folder.</summary>
    public static string SafeCombine(string folder, string fileName)
    {
        if (PlanValidator.FileNameProblem(fileName) is { } problem)
            throw new ToolException("file_name_invalid", $"File name {problem}", ErrorCategory.Validation);

        var root = Normalize(folder);
        var target = Path.GetFullPath(Path.Combine(root, fileName));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ToolException("path_escape", "The file would be saved outside the allowed folder.", ErrorCategory.Validation);

        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint) && dir.LinkTarget is not null)
                throw new ToolException("path_junction", $"{dir.FullName} is a link/junction, which KASPER does not follow.", ErrorCategory.Validation);
        }
        return target;
    }

    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Folders that must never be allowed (system locations, drive roots).</summary>
    public static string? FolderProblem(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return "Use a full path like C:\\Users\\you\\Documents\\Reports.";
        var full = Normalize(path);
        if (Path.GetPathRoot(full)?.TrimEnd('\\') == full.TrimEnd('\\')) return "A whole drive cannot be allowed. Pick a folder.";
        string[] blocked =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Path.GetFullPath(Application.KasperPaths.Root),
        ];
        foreach (var b in blocked.Where(b => !string.IsNullOrEmpty(b)))
        {
            var nb = Normalize(b);
            if (full.Equals(nb, StringComparison.OrdinalIgnoreCase) || full.StartsWith(nb + "\\", StringComparison.OrdinalIgnoreCase))
                return "System and KASPER data folders cannot be allowed.";
        }
        if (!Directory.Exists(full)) return "That folder does not exist.";
        return null;
    }
}
