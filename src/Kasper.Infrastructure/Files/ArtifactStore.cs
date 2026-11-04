using System.Security.Cryptography;
using System.Text;
using Kasper.Application;
using Kasper.Application.Abstractions;
using Kasper.Domain.Entities;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kasper.Infrastructure.Files;

/// <summary>
/// Files produced by workflows. They are written into a private staging folder per workflow
/// (%LOCALAPPDATA%\KASPER\staging\{workflowId}) and only leave it through an approved commit.
/// </summary>
public sealed class ArtifactStore(KasperDbContext db, IClock clock)
{
    public static string StagingDir(Guid workflowId) => Path.Combine(KasperPaths.Staging, workflowId.ToString("N"));

    public async Task<Artifact> CreateTextAsync(Guid workflowId, Guid stepId, string fileName, string content, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var dir = Path.Combine(StagingDir(workflowId), id.ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        await File.WriteAllBytesAsync(path, bytes, ct);

        var artifact = Artifact.Create(id, workflowId, stepId, fileName, path, Sha256Hex(bytes), bytes.Length,
            MimeFor(fileName), clock.UtcNow);
        db.Artifacts.Add(artifact);
        await db.SaveChangesAsync(ct);
        return artifact;
    }

    /// <summary>Moves a produced file into the step's staging folder and records it.</summary>
    public async Task<Artifact> CreateFromFileAsync(Guid workflowId, Guid stepId, string sourcePath, string fileName, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var dir = Path.Combine(StagingDir(workflowId), id.ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.Move(sourcePath, path);
        var hash = await CurrentHashAsync(path, ct) ?? "";
        var artifact = Artifact.Create(id, workflowId, stepId, fileName, path, hash, new FileInfo(path).Length, MimeFor(fileName), clock.UtcNow);
        db.Artifacts.Add(artifact);
        await db.SaveChangesAsync(ct);
        return artifact;
    }

    /// <summary>A scratch folder for engine work, inside this workflow's staging.</summary>
    public static string WorkDir(Guid workflowId)
    {
        var dir = Path.Combine(StagingDir(workflowId), "work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Uploaded input files wait here until a workflow imports them.</summary>
    public static string UploadDir(Guid token) => Path.Combine(KasperPaths.Staging, "uploads", token.ToString("N"));

    public Task<Artifact?> FindByStepAsync(Guid stepId, CancellationToken ct) =>
        db.Artifacts.FirstOrDefaultAsync(a => a.StepId == stepId, ct);

    public Task<Artifact?> FindAsync(Guid id, CancellationToken ct) => db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, ct);

    /// <summary>Hash of the file as it is on disk right now (detects changes after approval).</summary>
    public static async Task<string?> CurrentHashAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static void DeleteStaging(Guid workflowId)
    {
        var dir = StagingDir(workflowId);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string MimeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".md" => "text/markdown",
        ".txt" or ".log" => "text/plain",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".html" or ".htm" => "text/html",
        ".xml" => "application/xml",
        ".yaml" or ".yml" => "application/yaml",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };
}
