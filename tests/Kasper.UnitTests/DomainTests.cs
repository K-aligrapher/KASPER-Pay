using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Domain.Rules;

namespace Kasper.UnitTests;

public class WorkflowStateMachineTests
{
    [Theory]
    [InlineData(WorkflowStatus.Pending, WorkflowStatus.Running)]
    [InlineData(WorkflowStatus.Running, WorkflowStatus.WaitingForApproval)]
    [InlineData(WorkflowStatus.WaitingForApproval, WorkflowStatus.Running)]
    [InlineData(WorkflowStatus.WaitingForApproval, WorkflowStatus.Cancelled)]
    [InlineData(WorkflowStatus.Running, WorkflowStatus.Succeeded)]
    [InlineData(WorkflowStatus.Running, WorkflowStatus.Failed)]
    [InlineData(WorkflowStatus.Failed, WorkflowStatus.Pending)]
    public void Allows_documented_transitions(WorkflowStatus from, WorkflowStatus to) =>
        Assert.True(WorkflowStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(WorkflowStatus.Pending, WorkflowStatus.Succeeded)]
    [InlineData(WorkflowStatus.WaitingForApproval, WorkflowStatus.Succeeded)] // never skip the approved commit
    [InlineData(WorkflowStatus.Succeeded, WorkflowStatus.Running)]
    [InlineData(WorkflowStatus.Cancelled, WorkflowStatus.Pending)]
    public void Rejects_other_transitions(WorkflowStatus from, WorkflowStatus to) =>
        Assert.False(WorkflowStateMachine.CanTransition(from, to));

    [Fact]
    public void Workflow_records_timestamps_and_failure()
    {
        var t0 = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        var wf = Workflow.Create("me", "convert report", "Convert report", t0);

        wf.TransitionTo(WorkflowStatus.Running, t0.AddSeconds(1));
        wf.TransitionTo(WorkflowStatus.Failed, t0.AddSeconds(2), "engine_missing", "LibreOffice not installed");

        Assert.Equal(t0.AddSeconds(1), wf.StartedAtUtc);
        Assert.Equal(t0.AddSeconds(2), wf.CompletedAtUtc);
        Assert.Equal("engine_missing", wf.FailureCode);

        wf.TransitionTo(WorkflowStatus.Pending, t0.AddSeconds(3));
        Assert.Null(wf.FailureCode);
        Assert.Null(wf.CompletedAtUtc);
    }

    [Fact]
    public void Workflow_throws_on_illegal_transition()
    {
        var wf = Workflow.Create("me", "x", "x", DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => wf.TransitionTo(WorkflowStatus.Succeeded, DateTime.UtcNow));
    }
}

public class ApprovalPolicyTests
{
    [Theory]
    [InlineData(EffectClass.Read, false, false)]
    [InlineData(EffectClass.LocalWrite, true, false)]   // writing to private staging
    [InlineData(EffectClass.LocalWrite, false, true)]   // saving a file anywhere else
    [InlineData(EffectClass.ExternalWrite, false, true)] // Notion / Slack
    [InlineData(EffectClass.Destructive, false, true)]
    [InlineData(EffectClass.Destructive, true, true)]    // destructive always asks
    public void Asks_before_saving_sending_updating_and_deleting(EffectClass effect, bool stagingOnly, bool expected) =>
        Assert.Equal(expected, ApprovalPolicy.RequiresApproval(effect, stagingOnly));
}
