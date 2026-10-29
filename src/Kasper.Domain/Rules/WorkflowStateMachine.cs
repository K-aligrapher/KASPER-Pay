using Kasper.Domain.Enums;

namespace Kasper.Domain.Rules;

/// <summary>Allowed workflow status transitions (PRD §13).</summary>
public static class WorkflowStateMachine
{
    private static readonly Dictionary<WorkflowStatus, WorkflowStatus[]> Allowed = new()
    {
        [WorkflowStatus.Pending] = [WorkflowStatus.Running, WorkflowStatus.Cancelled],
        [WorkflowStatus.Running] =
        [
            WorkflowStatus.WaitingForApproval,
            WorkflowStatus.Succeeded,
            WorkflowStatus.Failed,
            WorkflowStatus.Cancelled,
        ],
        [WorkflowStatus.WaitingForApproval] = [WorkflowStatus.Running, WorkflowStatus.Cancelled],
        [WorkflowStatus.Failed] = [WorkflowStatus.Pending],
        [WorkflowStatus.Succeeded] = [],
        [WorkflowStatus.Cancelled] = [],
    };

    public static bool CanTransition(WorkflowStatus from, WorkflowStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsTerminal(WorkflowStatus status) =>
        status is WorkflowStatus.Succeeded or WorkflowStatus.Cancelled;
}
