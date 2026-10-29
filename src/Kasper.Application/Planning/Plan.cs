using System.Text.Json.Nodes;

namespace Kasper.Application.Planning;

/// <summary>A constrained plan (PRD §16). Proposals only: the server validates every part.</summary>
public sealed record Plan(string Summary, IReadOnlyList<PlanStep> Steps, string? Reply = null);

public sealed record PlanStep(string Key, string ToolId, string Purpose, JsonObject Input);

/// <summary>Either a conversational reply (no tools needed) or a validated plan.</summary>
public sealed record PlannerResult(string? Reply, Plan? Plan, string? Error = null);

public interface IPlanner
{
    Task<PlannerResult> PlanAsync(IReadOnlyList<Contracts.ChatMessageDto> conversation, CancellationToken ct);
}
