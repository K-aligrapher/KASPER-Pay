using System.Diagnostics;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;

namespace Kasper.Infrastructure.Documents;

/// <summary>Finds external document engines (PATH or their usual install folders).</summary>
public static class EngineLocator
{
    private static readonly Dictionary<string, string[]> Candidates = new()
    {
        ["LibreOffice"] = ["soffice", @"C:\Program Files\LibreOffice\program\soffice.exe", @"C:\Program Files (x86)\LibreOffice\program\soffice.exe"],
        ["Pandoc"] = ["pandoc", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Pandoc\pandoc.exe"), @"C:\Program Files\Pandoc\pandoc.exe"],
        ["Ghostscript"] = ["gswin64c", "gs"],
        ["QPDF"] = ["qpdf"],
        ["Tesseract"] = ["tesseract", @"C:\Program Files\Tesseract-OCR\tesseract.exe"],
        ["pdf2docx"] = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"KASPER\engines\py\Scripts\pdf2docx.exe"), "pdf2docx"],
    };

    /// <summary>Installers that put engines in versioned folders not on PATH (e.g. "C:\Program Files\qpdf 12.4.2\bin").</summary>
    private static readonly Dictionary<string, (string Pattern, string Exe)[]> VersionedDirs = new()
    {
        ["Ghostscript"] = [(@"C:\Program Files\gs", "gswin64c.exe"), (@"C:\Program Files (x86)\gs", "gswin32c.exe")],
        ["QPDF"] = [(@"C:\Program Files", "qpdf.exe"), (@"C:\Program Files (x86)", "qpdf.exe")],
    };

    // Ghostscript is no longer in winget; scoop (or the official installer) works.
    public static readonly Dictionary<string, string> InstallHints = new()
    {
        ["LibreOffice"] = "winget install TheDocumentFoundation.LibreOffice",
        ["Pandoc"] = "winget install JohnMacFarlane.Pandoc",
        ["Ghostscript"] = "scoop install ghostscript  (or ghostscript.com/releases)",
        ["QPDF"] = "winget install QPDF.QPDF",
        ["Tesseract"] = "winget install UB-Mannheim.TesseractOCR",
        ["pdf2docx"] = @"python -m venv %LOCALAPPDATA%\KASPER\engines\py; then %LOCALAPPDATA%\KASPER\engines\py\Scripts\pip install pdf2docx",
    };

    /// <summary>
    /// PATH of this process plus the current user/machine PATH from the registry, so engines
    /// installed after KASPER started are found without a restart.
    /// </summary>
    private static IEnumerable<string> SearchPath() =>
        new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims"),
        }
        .SelectMany(p => (p ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        .Select(d => Environment.ExpandEnvironmentVariables(d.Trim().Trim('"')))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    public static string? Find(string engine)
    {
        foreach (var c in Candidates[engine])
        {
            if (Path.IsPathRooted(c)) { if (File.Exists(c)) return c; continue; }
            foreach (var dir in SearchPath())
            {
                var p = Path.Combine(dir, c + ".exe");
                if (File.Exists(p)) return p;
            }
        }
        foreach (var (root, exe) in VersionedDirs.GetValueOrDefault(engine, []))
        {
            if (!Directory.Exists(root)) continue;
            var dirs = engine == "QPDF"
                ? Directory.EnumerateDirectories(root, "qpdf*").Select(d => Path.Combine(d, "bin"))
                : Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "bin"));
            var hit = dirs.Select(d => Path.Combine(d, exe)).Where(File.Exists).OrderDescending().FirstOrDefault();
            if (hit is not null) return hit;
        }
        return null;
    }

    public static string Require(string engine) => Find(engine)
        ?? throw new ToolException("engine_missing", $"{engine} is not installed. Install it with: {InstallHints[engine]}", ErrorCategory.EngineMissing);
}

/// <summary>Runs an engine as a child process with a timeout; cancelling kills the whole process tree (FR-023).</summary>
public static class ProcessRunner
{
    public static async Task RunAsync(string exe, IEnumerable<string> args, string workDir, CancellationToken ct, int[]? okExitCodes = null)
    {
        okExitCodes ??= [0];
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new ToolException("engine_start", $"Could not start {Path.GetFileName(exe)}.", ErrorCategory.Permanent);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (!okExitCodes.Contains(p.ExitCode))
        {
            var err = (await stderr).Trim();
            throw new ToolException("engine_failed",
                $"{Path.GetFileNameWithoutExtension(exe)} failed (exit {p.ExitCode}){(err.Length > 0 ? ": " + (err.Length > 300 ? err[..300] : err) : "")}",
                ErrorCategory.Permanent);
        }
    }
}
