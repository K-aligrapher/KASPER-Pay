using Kasper.Application.Contracts;
using Kasper.Application.Planning;

namespace Kasper.Infrastructure.Runtime;

/// <summary>One entry point for the command bar and the CLI: answer, or start a workflow.</summary>
public sealed class AssistantService(IPlanner planner, WorkflowService workflows)
{
    public async Task<AssistantResponseDto> HandleAsync(IReadOnlyList<ChatMessageDto> messages, CancellationToken ct)
    {
        var result = await planner.PlanAsync(messages, ct);
        if (result.Plan is null)
            return new AssistantResponseDto("reply", result.Reply ?? result.Error ?? "Sorry, I have no answer for that.", null);

        var request = messages.LastOrDefault(m => m.Role == "user")?.Content ?? result.Plan.Summary;
        try
        {
            var summary = await workflows.CreateAsync(request, result.Plan, ct);
            return new AssistantResponseDto("workflow", result.Reply ?? $"On it: {summary.Summary}.", summary);
        }
        catch (WorkflowRequestException ex)
        {
            return new AssistantResponseDto("reply", $"I couldn't start that safely: {string.Join(" ", ex.Details.DefaultIfEmpty(ex.Message))}", null);
        }
    }
}
