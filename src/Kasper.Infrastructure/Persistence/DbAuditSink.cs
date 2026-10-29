using Kasper.Application.Abstractions;
using Kasper.Domain.Entities;

namespace Kasper.Infrastructure.Persistence;

public sealed class DbAuditSink(KasperDbContext db) : IAuditSink
{
    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken ct)
    {
        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(ct);
    }
}
