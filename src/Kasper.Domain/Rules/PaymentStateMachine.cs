using Kasper.Domain.Enums;

namespace Kasper.Domain.Rules;

/// <summary>Allowed payment transitions. Anything else throws, so money states can't skip a step.</summary>
public static class PaymentStateMachine
{
    private static readonly Dictionary<PaymentStatus, PaymentStatus[]> Allowed = new()
    {
        [PaymentStatus.Proposed] = [PaymentStatus.Approved, PaymentStatus.Rejected, PaymentStatus.Blocked, PaymentStatus.Expired, PaymentStatus.Cancelled],
        [PaymentStatus.Approved] = [PaymentStatus.CheckoutCreated, PaymentStatus.Sent, PaymentStatus.Blocked, PaymentStatus.Cancelled, PaymentStatus.Expired, PaymentStatus.Failed],
        [PaymentStatus.CheckoutCreated] = [PaymentStatus.PayerApproved, PaymentStatus.Cancelled, PaymentStatus.Expired, PaymentStatus.Failed],
        [PaymentStatus.PayerApproved] = [PaymentStatus.Captured, PaymentStatus.Failed, PaymentStatus.Expired],
        [PaymentStatus.Captured] = [],
        [PaymentStatus.Sent] = [],
        [PaymentStatus.Rejected] = [],
        [PaymentStatus.Blocked] = [],
        [PaymentStatus.Cancelled] = [],
        [PaymentStatus.Expired] = [],
        [PaymentStatus.Failed] = [],
    };

    public static bool CanTransition(PaymentStatus from, PaymentStatus to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public static bool IsTerminal(PaymentStatus s) => Allowed[s].Length == 0;

    /// <summary>Statuses whose money has been (or is about to be) committed; these count against spending limits.</summary>
    public static bool CountsAsSpend(PaymentStatus s) =>
        s is PaymentStatus.Approved or PaymentStatus.CheckoutCreated or PaymentStatus.PayerApproved or PaymentStatus.Captured;
}
