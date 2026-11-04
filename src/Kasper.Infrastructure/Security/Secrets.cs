using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kasper.Application;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;

namespace Kasper.Infrastructure.Security;

/// <summary>Credentials encrypted with Windows DPAPI for the current user (FR-007). One file per integration.</summary>
public sealed class DpapiStore<T>(string name) where T : class
{
    private string FilePath => Path.Combine(KasperPaths.Root, "secrets", name + ".bin");
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes("KASPER." + name + ".v1");

    public bool Exists => File.Exists(FilePath);

    public T? Load()
    {
        if (!File.Exists(FilePath) || !OperatingSystem.IsWindows()) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), _entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return null; }
    }

    public void Save(T value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Secrets need Windows DPAPI.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), _entropy, DataProtectionScope.CurrentUser));
    }

    public void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}

/// <summary>
/// One-time secrets such as PDF passwords (NFR-012): kept in memory only, referenced from workflow
/// inputs as "secret:{id}", so they never reach the database or logs. Lost on restart by design.
/// </summary>
public sealed class EphemeralSecrets
{
    private readonly ConcurrentDictionary<string, string> _values = new();

    public string Put(string value)
    {
        var key = "secret:" + Guid.NewGuid().ToString("N");
        _values[key] = value;
        return key;
    }

    public string Get(string reference) => _values.TryGetValue(reference, out var v)
        ? v
        : throw new ToolException("secret_gone", "The password is no longer in memory (KASPER restarted). Start the task again.", ErrorCategory.Permanent);

    public void Forget(string reference) => _values.TryRemove(reference, out _);
}
