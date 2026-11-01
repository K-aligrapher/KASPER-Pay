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
    };

    public static readonly Dictionary<string, string> InstallHints = new()
    {
        ["LibreOffice"] = "winget install TheDocumentFoundation.LibreOffice",
        ["Pandoc"] = "winget install JohnMacFarlane.Pandoc",
        ["Ghostscript"] = "winget install ArtifexSoftware.GhostScript",
        ["QPDF"] = "winget install QPDF.QPDF",
        ["Tesseract"] = "winget install UB-Mannheim.TesseractOCR",
    };

    public static string? Find(string engine)
    {
        foreach (var c in Candidates[engine])
        {
            if (Path.IsPathRooted(c)) { if (File.Exists(c)) return c; continue; }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = Path.Combine(dir.Trim('"'), c + ".exe");
                if (File.Exists(p)) return p;
            }
        }
        if (engine == "Ghostscript")
        {
            var root = @"C:\Program Files\gs";
            if (Directory.Exists(root))
                return Directory.EnumerateFiles(root, "gswin64c.exe", SearchOption.AllDirectories).OrderDescending().FirstOrDefault();
        }
        return null;
    }

    public static string Require(string engine) => Find(engine)
        ?? throw new ToolException("engine_missing", $"{engine} is not installed. Install it with: {InstallHints[engine]}", ErrorCategory.EngineMissing);
}

/// <summary>Runs an engine as a child process with a timeout; cancelling kills the whole process tree (FR-023).</summary>
public static class ProcessRunner
{
    public static async Task RunAsync(string exe, IEnumerable<string> args, string workDir, CancellationToken ct)
    {
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
        if (p.ExitCode != 0)
        {
            var err = (await stderr).Trim();
            throw new ToolException("engine_failed",
                $"{Path.GetFileNameWithoutExtension(exe)} failed (exit {p.ExitCode}){(err.Length > 0 ? ": " + (err.Length > 300 ? err[..300] : err) : "")}",
                ErrorCategory.Permanent);
        }
    }
}
