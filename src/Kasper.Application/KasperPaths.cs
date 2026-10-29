namespace Kasper.Application;

/// <summary>Well-known local folders. Everything lives under %LOCALAPPDATA%\KASPER.</summary>
public static class KasperPaths
{
    public static string Root { get; } = Environment.GetEnvironmentVariable("KASPER_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KASPER");

    public static string SessionFile => Path.Combine(Root, "session.json");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string Database => Path.Combine(Root, "kasper.db");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Staging => Path.Combine(Root, "staging");
    public static string WebViewData => Path.Combine(Root, "webview");

    public static void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Logs, Staging, WebViewData })
            Directory.CreateDirectory(dir);
    }
}
