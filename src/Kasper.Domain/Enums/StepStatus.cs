namespace Kasper.Domain.Enums;

public enum StepStatus
{
    Pending,
    Running,
    WaitingForApproval,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Failure categories (NFR-023). Only Transient and RateLimit are retried.</summary>
public enum ErrorCategory
{
    Transient,
    RateLimit,
    Timeout,
    Permanent,
    Validation,
    Authorization,
    EngineMissing,
    Cancelled,
    Interrupted,
}
