namespace Kasper.Domain.Enums;

public enum WorkflowStatus
{
    Pending,
    Running,
    WaitingForApproval,
    Succeeded,
    Failed,
    Cancelled,
}
