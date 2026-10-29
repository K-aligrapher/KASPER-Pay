namespace Kasper.Domain.Entities;

/// <summary>A file KASPER produced. It lives in staging until an approved commit moves it.</summary>
public sealed class Artifact
{
    private Artifact() { }

    public Guid Id { get; private set; }
    public Guid WorkflowId { get; private set; }
    public Guid StepId { get; private set; }
    public string FileName { get; private set; } = "";
    public string StagingPath { get; private set; } = "";
    public string? CommittedPath { get; private set; }
    public string Sha256 { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string MimeType { get; private set; } = "application/octet-stream";
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CommittedAtUtc { get; private set; }

    public static Artifact Create(Guid id, Guid workflowId, Guid stepId, string fileName, string stagingPath,
        string sha256, long size, string mimeType, DateTime nowUtc) => new()
    {
        Id = id,
        WorkflowId = workflowId,
        StepId = stepId,
        FileName = fileName,
        StagingPath = stagingPath,
        Sha256 = sha256,
        SizeBytes = size,
        MimeType = mimeType,
        CreatedAtUtc = nowUtc,
    };

    public void MarkCommitted(string path, DateTime nowUtc)
    {
        CommittedPath = path;
        CommittedAtUtc = nowUtc;
    }
}

/// <summary>A folder KASPER may save into (FR-005). Referenced in plans as "folder:{Id}".</summary>
public sealed class AllowedFolder
{
    public int Id { get; set; }
    public required string Path { get; set; }
    public DateTime AddedAtUtc { get; set; }
    public bool IsDefault { get; set; }
}
