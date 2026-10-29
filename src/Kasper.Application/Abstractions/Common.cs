using Kasper.Application.Contracts;
using Kasper.Domain.Entities;

namespace Kasper.Application.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
}

public interface IAuditSink
{
    Task RecordAsync(AuditEvent auditEvent, CancellationToken ct);
}

public interface IIntegrationHealthCheck
{
    string Name { get; }
    Task<ComponentStatusDto> CheckAsync(CancellationToken ct);
}

/// <summary>Implemented by the desktop shell; lets the API (and the CLI through it) control the window.</summary>
public interface IDesktopShell
{
    void ShowMainWindow();
    void Notify(string title, string message);
}

/// <summary>Writes and reads <see cref="SessionInfo"/> so the CLI can reach the running app.</summary>
public interface ISessionStore
{
    void Write(SessionInfo session);
    SessionInfo? Read();
    void Delete(int processId);
}
