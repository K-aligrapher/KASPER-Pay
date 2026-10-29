using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Kasper.Application;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;

namespace Kasper.Infrastructure.Security;

/// <summary>
/// Stores the per-launch API port and token for the CLI. On Windows the file is restricted
/// to the current user (inherited permissions removed), so other accounts cannot read the token.
/// </summary>
public sealed class SessionStore(string? path = null) : ISessionStore
{
    private readonly string _path = path ?? KasperPaths.SessionFile;

    public void Write(SessionInfo session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(session));
        if (OperatingSystem.IsWindows()) RestrictToCurrentUser(tmp);
        File.Move(tmp, _path, overwrite: true);
    }

    public SessionInfo? Read()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(_path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Delete(int processId)
    {
        if (Read() is { } s && s.ProcessId == processId)
        {
            try { File.Delete(_path); } catch (IOException) { }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUser(string file)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }
}
