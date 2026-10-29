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

/// <summary>Implemented by the desktop shell; lets the API and worker reach the window and tray.</summary>
public interface IDesktopShell
{
    /// <param name="route">Optional view to open, e.g. "approvals".</param>
    void ShowMainWindow(string? route = null);

    void Notify(string title, string message, string? route = null);
}

/// <summary>Used when no desktop is attached (tests, headless).</summary>
public sealed class NullDesktopShell : IDesktopShell
{
    public void ShowMainWindow(string? route = null) { }
    public void Notify(string title, string message, string? route = null) { }
}

/// <summary>Writes and reads <see cref="SessionInfo"/> so the CLI can reach the running app.</summary>
public interface ISessionStore
{
    void Write(SessionInfo session);
    SessionInfo? Read();
    void Delete(int processId);
}

/// <summary>Live workflow updates for the UI (SSE) and a wake-up signal for the worker.</summary>
public interface IWorkflowEvents
{
    void Publish(WorkflowEventDto e);
    IAsyncEnumerable<WorkflowEventDto> SubscribeAsync(CancellationToken ct);

    /// <summary>Tell the worker there is something to do.</summary>
    void SignalWorker();
    Task WaitForSignalAsync(TimeSpan timeout, CancellationToken ct);
}
