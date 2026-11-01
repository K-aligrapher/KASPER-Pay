using System.Diagnostics;
using System.IO;

namespace Kasper.Desktop;

/// <summary>Writes startup stage timings to %LOCALAPPDATA%\KASPER\logs\startup.log.</summary>
internal static class StartupTimer
{
    public static void Mark(string stage)
    {
        try
        {
            var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            var dir = Path.Combine(Kasper.Application.KasperPaths.Root, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "startup.log"), $"{DateTime.Now:HH:mm:ss.fff} {ms,7:0} ms  {stage}{Environment.NewLine}");
        }
        catch (IOException) { }
    }
}
