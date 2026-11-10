namespace Kasper.Domain.Enums;

/// <summary>Purchase lifecycle (PayPal PRD F9). Happy path first, then terminal side states.</summary>
public enum PaymentStatus
{
    Proposed,
    Approved,
    CheckoutCreated,
    PayerApproved,
    Captured,
    Rejected,
    Blocked,
    Cancelled,
    Expired,
    Failed,
}
