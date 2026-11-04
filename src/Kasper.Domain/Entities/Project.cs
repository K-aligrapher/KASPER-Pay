namespace Kasper.Domain.Entities;

/// <summary>A project links a local folder, Notion, a Slack channel and a GitHub repo (FR-300).</summary>
public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Active";
    public string? FolderPath { get; set; }
    public string? NotionQuery { get; set; }
    public string? SlackChannel { get; set; }
    public string? GitHubRepo { get; set; }
    /// <summary>Local Markdown notes (FR-303).</summary>
    public string Notes { get; set; } = "";
    public string? LastBriefing { get; set; }
    public DateTime? LastBriefingAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A recurring request (PRD Phase 6). It still stops at Final Review before any write.</summary>
public sealed class Schedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Request { get; set; }
    /// <summary>Local time "HH:mm".</summary>
    public required string TimeOfDay { get; set; }
    /// <summary>"daily", "weekdays" or comma list like "Mon,Thu".</summary>
    public string Days { get; set; } = "daily";
    public bool Enabled { get; set; } = true;
    public DateTime? LastRunUtc { get; set; }
    public Guid? LastWorkflowId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public bool RunsOn(DayOfWeek day) => Days.Trim().ToLowerInvariant() switch
    {
        "daily" or "" => true,
        "weekdays" => day is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        var list => list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(d => d.Length >= 3 && day.ToString().StartsWith(d[..3], StringComparison.OrdinalIgnoreCase)),
    };
}
