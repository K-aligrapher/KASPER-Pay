using Kasper.Domain.Enums;
using Kasper.Domain.Rules;

namespace Kasper.Domain.Entities;

/// <summary>
/// One purchase. Amount and currency are copied from the catalog by code, never from the model.
/// One row per workflow step, so approving twice can never create two payments.
/// </summary>
public sealed class Payment
{
    private Payment() { }

    public Guid Id { get; private set; }
    public Guid? WorkflowId { get; private set; }
    /// <summary>Unique when set: the idempotency anchor for double clicks and restarts.</summary>
    public Guid? StepId { get; private set; }
    public string ProductId { get; private set; } = "";
    public string ProductName { get; private set; } = "";
    public string Merchant { get; private set; } = "";
    public int Quantity { get; private set; }
    public decimal UnitAmount { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public string? Reason { get; private set; }
    public PaymentStatus Status { get; private set; }
    /// <summary>Rule that blocked this purchase (set only when Blocked).</summary>
    public string? BlockedRule { get; private set; }
    public string? BlockedMessage { get; private set; }
    public string Fingerprint { get; private set; } = "";
    public string? PayPalOrderId { get; private set; }
    public string? PayPalCaptureId { get; private set; }
    public string? ApproveUrl { get; private set; }
    public string? FailureSummary { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static Payment Propose(Guid? workflowId, Guid? stepId, string productId, string productName, string merchant,
        int quantity, decimal unitAmount, string currency, string? reason, string fingerprint, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = workflowId,
        StepId = stepId,
        ProductId = productId,
        ProductName = productName,
        Merchant = merchant,
        Quantity = quantity,
        UnitAmount = unitAmount,
        Amount = unitAmount * quantity,
        Currency = currency,
        Reason = reason is { Length: > 300 } ? reason[..300] : reason,
        Fingerprint = fingerprint,
        Status = PaymentStatus.Proposed,
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc,
    };

    /// <summary>Re-propose after the action changed (new fingerprint). Only valid while still Proposed.</summary>
    public void Refresh(string fingerprint, DateTime nowUtc)
    {
        if (Status != PaymentStatus.Proposed) throw new InvalidOperationException($"Cannot change a {Status} payment.");
        Fingerprint = fingerprint;
        UpdatedAtUtc = nowUtc;
    }

    public void TransitionTo(PaymentStatus next, DateTime nowUtc)
    {
        if (!PaymentStateMachine.CanTransition(Status, next))
            throw new InvalidOperationException($"Invalid payment transition {Status} → {next}.");
        Status = next;
        UpdatedAtUtc = nowUtc;
    }

    public void Block(string rule, string message, DateTime nowUtc)
    {
        TransitionTo(PaymentStatus.Blocked, nowUtc);
        BlockedRule = rule;
        BlockedMessage = message.Length > 500 ? message[..500] : message;
    }

    public void Fail(string summary, DateTime nowUtc)
    {
        TransitionTo(PaymentStatus.Failed, nowUtc);
        FailureSummary = summary.Length > 500 ? summary[..500] : summary;
    }

    public void CheckoutCreated(string orderId, string? approveUrl, DateTime nowUtc)
    {
        TransitionTo(PaymentStatus.CheckoutCreated, nowUtc);
        PayPalOrderId = orderId;
        ApproveUrl = approveUrl;
    }

    public void Captured(string? captureId, DateTime nowUtc)
    {
        TransitionTo(PaymentStatus.Captured, nowUtc);
        PayPalCaptureId = captureId;
    }
}
