namespace Kasper.Application.Contracts;

/// <summary>
/// Written by the running desktop process so the CLI can find the local API.
/// Stored in %LOCALAPPDATA%\KASPER\session.json, readable by the current user only.
/// </summary>
public sealed record SessionInfo(int Port, string Token, int ProcessId, DateTime StartedAtUtc)
{
    public string BaseUrl => $"http://127.0.0.1:{Port}";
}
