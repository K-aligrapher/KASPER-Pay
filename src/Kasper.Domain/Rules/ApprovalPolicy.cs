using Kasper.Domain.Enums;

namespace Kasper.Domain.Rules;

/// <summary>
/// Owner decision (2026-10-30): ask before sending, updating, deleting and saving.
/// Only reads and writes into the private staging area run without Final Review.
/// </summary>
public static class ApprovalPolicy
{
    public static bool RequiresApproval(EffectClass effect, bool writesOnlyToStaging) => effect switch
    {
        EffectClass.Read => false,
        EffectClass.LocalWrite => !writesOnlyToStaging,
        _ => true,
    };
}
