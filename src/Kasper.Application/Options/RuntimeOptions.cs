namespace Kasper.Application.Options;

public sealed class RuntimeOptions
{
    public const string Section = "Runtime";

    /// <summary>Maximum steps per plan (FR-016).</summary>
    public int MaxSteps { get; set; } = 8;

    /// <summary>How long a Final Review stays valid before it must be re-requested.</summary>
    public int ApprovalLifetimeHours { get; set; } = 24;

    /// <summary>Failed workflows keep their staging files this long for inspection.</summary>
    public int FailedStagingRetentionDays { get; set; } = 7;

    /// <summary>Exposes mock.* tools for testing the runtime. Off for normal use.</summary>
    public bool EnableMockTools { get; set; }

    /// <summary>Base delay for retry backoff (doubles per attempt, plus jitter).</summary>
    public int RetryBaseDelayMs { get; set; } = 500;
}
