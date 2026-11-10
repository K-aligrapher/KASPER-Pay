using System.Text.Json;
using System.Text.Json.Nodes;
using Kasper.Application;
using Kasper.Application.Abstractions;
using Kasper.Application.Commerce;
using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Commerce;

public sealed record PaymentDto(Guid Id, Guid? WorkflowId, string Kind, string Status, string Item, string Merchant, string? Recipient,
    decimal Amount, string Currency, string? Reason, string? BlockedRule, string? BlockedMessage, string? PayPalOrderId,
    string? PayPalCaptureId, string? PayPalInvoiceId, string? FailureSummary, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed record GuardrailsDto(decimal PerTransactionLimit, decimal DailyLimit, int DuplicateWindowMinutes, string Currency);

/// <summary>History of payments and invoices, plus the spending-limit settings. Never exposes secrets.</summary>
public sealed class PaymentService(KasperDbContext db, IClock clock, IOptionsMonitor<PaymentOptions> options)
{
    public async Task<IReadOnlyList<PaymentDto>> ListAsync(int take, CancellationToken ct)
    {
        await SyncRejectedAsync(ct);
        var rows = await db.Payments.AsNoTracking().OrderByDescending(p => p.CreatedAtUtc).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<PaymentDto?> GetAsync(Guid id, CancellationToken ct)
    {
        await SyncRejectedAsync(ct);
        return await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) is { } p ? ToDto(p) : null;
    }

    /// <summary>A proposal whose workflow was rejected or cancelled in Final Review will never be paid.</summary>
    public async Task SyncRejectedAsync(CancellationToken ct)
    {
        var open = await db.Payments.Where(p => p.Status == PaymentStatus.Proposed && p.WorkflowId != null).ToListAsync(ct);
        if (open.Count == 0) return;
        var ids = open.Select(p => p.WorkflowId!.Value).Distinct().ToList();
        var dead = await db.Workflows.Where(w => ids.Contains(w.Id) && w.Status == WorkflowStatus.Cancelled).Select(w => w.Id).ToListAsync(ct);
        foreach (var p in open.Where(p => dead.Contains(p.WorkflowId!.Value))) p.TransitionTo(PaymentStatus.Rejected, clock.UtcNow);
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }

    public GuardrailsDto Guardrails() => ToDto(options.CurrentValue);

    /// <summary>Saves the limits to settings.json; the running app picks them up live.</summary>
    public async Task<GuardrailsDto> SaveGuardrailsAsync(GuardrailsDto dto, CancellationToken ct)
    {
        if (dto.PerTransactionLimit is <= 0 or > 100_000 || dto.DailyLimit is <= 0 or > 1_000_000 || dto.DuplicateWindowMinutes is < 0 or > 1440)
            throw new ArgumentException("Limits must be positive amounts; the duplicate window must be 0–1440 minutes.");
        var path = KasperPaths.SettingsFile;
        var root = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject ?? [] : [];
        if (root["Payments"] is not JsonObject pay) root["Payments"] = pay = [];
        pay["PerTransactionLimit"] = decimal.Round(dto.PerTransactionLimit, 2);
        pay["DailyLimit"] = decimal.Round(dto.DailyLimit, 2);
        pay["DuplicateWindowMinutes"] = dto.DuplicateWindowMinutes;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        File.Move(tmp, path, overwrite: true);
        db.AuditEvents.Add(new AuditEvent
        {
            Actor = Environment.UserName, EventType = "settings.guardrails", Outcome = "ok", TimestampUtc = clock.UtcNow,
            MetadataJson = JsonSerializer.Serialize(new { dto.PerTransactionLimit, dto.DailyLimit, dto.DuplicateWindowMinutes }),
        });
        await db.SaveChangesAsync(ct);
        return new GuardrailsDto(decimal.Round(dto.PerTransactionLimit, 2), decimal.Round(dto.DailyLimit, 2), dto.DuplicateWindowMinutes, options.CurrentValue.Currency);
    }

    private static GuardrailsDto ToDto(PaymentOptions o) => new(o.PerTransactionLimit, o.DailyLimit, o.DuplicateWindowMinutes, o.Currency);

    private static PaymentDto ToDto(Payment p) => new(p.Id, p.WorkflowId, p.Kind, p.Status.ToString(), p.ProductName, p.Merchant, p.Recipient,
        p.Amount, p.Currency, p.Reason, p.BlockedRule, p.BlockedMessage, p.PayPalOrderId, p.PayPalCaptureId, p.PayPalInvoiceId,
        p.FailureSummary, p.CreatedAtUtc, p.UpdatedAtUtc);
}
