using System.Text.Json.Nodes;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Documents;
using Kasper.Infrastructure.Files;
using Kasper.Infrastructure.Security;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace Kasper.Infrastructure.Tools;

/// <summary>pdf.stamp: text watermark and/or page numbers (FR-202), in-process with PDFsharp.</summary>
public sealed class PdfStampTool(ArtifactStore artifacts) : DocumentToolBase(artifacts), ITool
{
    static PdfStampTool()
    {
        // Use installed Windows fonts (Arial) instead of requiring a custom font resolver.
        if (OperatingSystem.IsWindows()) PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    public ToolDescriptor Descriptor { get; } = Pdf("pdf.stamp", "Add a text watermark and/or page numbers to a PDF.",
    [
        new ToolParameter("artifact", ParamType.ArtifactRef, true, "The PDF."),
        new ToolParameter("watermark", ParamType.Text, false, "Watermark text, e.g. DRAFT.", 60),
        new ToolParameter("pageNumbers", ParamType.Text, false, "\"yes\" to add page numbers.", 3),
    ]);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var a = await ArtifactAsync(input["artifact"], ct);
        PdfMergeTool.RequirePdf([a]);
        var watermark = Input.OptStr(input, "watermark");
        var numbers = Input.OptStr(input, "pageNumbers") == "yes";
        if (watermark is null && !numbers)
            throw new ToolException("nothing_to_add", "Choose a watermark, page numbers, or both.", ErrorCategory.Validation);

        var output = Path.Combine(ArtifactStore.WorkDir(context.WorkflowId), "stamped.pdf");
        File.Copy(a.StagingPath, output);
        using (var doc = PdfReader.Open(output, PdfDocumentOpenMode.Modify))
        {
            var markFont = new XFont("Arial", 60, XFontStyleEx.Bold);
            var numFont = new XFont("Arial", 10);
            for (var i = 0; i < doc.PageCount; i++)
            {
                var page = doc.Pages[i];
                using var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var w = page.Width.Point;
                var h = page.Height.Point;
                if (watermark is not null)
                {
                    var state = g.Save();
                    g.TranslateTransform(w / 2, h / 2);
                    g.RotateTransform(-45);
                    g.DrawString(watermark, markFont, new XSolidBrush(XColor.FromArgb(60, 160, 160, 160)), 0, 0, XStringFormats.Center);
                    g.Restore(state);
                }
                if (numbers)
                    g.DrawString($"{i + 1} / {doc.PageCount}", numFont, XBrushes.Gray, w / 2, h - 20, XStringFormats.Center);
            }
            doc.Save(output);
        }
        return await StageAsync(context, output, $"{Stem(a)}-stamped.pdf", ct);
    }
}

/// <summary>
/// pdf.protect / pdf.unlock via QPDF (AES-256). Passwords arrive as one-time in-memory secrets
/// and are passed to QPDF in a temporary argument file, never on the command line (NFR-012).
/// </summary>
public sealed class PdfPasswordTool(ArtifactStore artifacts, EphemeralSecrets secrets, bool protect) : DocumentToolBase(artifacts), ITool
{
    public ToolDescriptor Descriptor { get; } = Pdf(protect ? "pdf.protect" : "pdf.unlock",
        protect ? "Protect a PDF with a password (you type it in Documents)." : "Remove the password from a PDF you own (you supply the password).",
        [
            new ToolParameter("artifact", ParamType.ArtifactRef, true, "The PDF."),
            new ToolParameter("password", ParamType.Secret, true, "Entered by you; never stored."),
        ]) with { HiddenFromPlanner = true };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var a = await ArtifactAsync(input["artifact"], ct);
        PdfMergeTool.RequirePdf([a]);
        var secretRef = Input.Str(input, "password");
        var password = secrets.Get(secretRef);
        var work = ArtifactStore.WorkDir(context.WorkflowId);
        var output = Path.Combine(work, protect ? "protected.pdf" : "unlocked.pdf");
        var argFile = Path.Combine(work, "args.txt");
        try
        {
            var args = protect
                ? new[] { "--encrypt", password, password, "256", "--", a.StagingPath, output }
                : new[] { "--password=" + password, "--decrypt", a.StagingPath, output };
            await File.WriteAllLinesAsync(argFile, args, ct);
            try
            {
                await ProcessRunner.RunAsync(EngineLocator.Require("QPDF"), ["@" + argFile], work, ct, okExitCodes: [0, 3]);
            }
            catch (ToolException ex) when (!protect && ex.Code == "engine_failed")
            {
                throw new ToolException("wrong_password", "That password didn't open the PDF.", ErrorCategory.Validation);
            }
        }
        finally
        {
            if (File.Exists(argFile)) File.Delete(argFile);
        }
        secrets.Forget(secretRef);
        return await StageAsync(context, output, $"{Stem(a)}-{(protect ? "protected" : "unlocked")}.pdf", ct);
    }
}

public sealed class PdfProtectTool(ArtifactStore artifacts, EphemeralSecrets secrets) : ITool
{
    private readonly PdfPasswordTool _inner = new(artifacts, secrets, protect: true);
    public ToolDescriptor Descriptor => _inner.Descriptor;
    public Task<ToolResult> ExecuteAsync(ToolContext c, JsonObject i, CancellationToken ct) => _inner.ExecuteAsync(c, i, ct);
}

public sealed class PdfUnlockTool(ArtifactStore artifacts, EphemeralSecrets secrets) : ITool
{
    private readonly PdfPasswordTool _inner = new(artifacts, secrets, protect: false);
    public ToolDescriptor Descriptor => _inner.Descriptor;
    public Task<ToolResult> ExecuteAsync(ToolContext c, JsonObject i, CancellationToken ct) => _inner.ExecuteAsync(c, i, ct);
}

/// <summary>pdf.ocr: scanned PDF/image → searchable PDF (FR-203). Ghostscript renders pages, Tesseract adds text.</summary>
public sealed class OcrTool(ArtifactStore artifacts) : DocumentToolBase(artifacts), ITool
{
    public ToolDescriptor Descriptor { get; } = Pdf("pdf.ocr", "Make a scanned PDF or image searchable (OCR).",
    [
        new ToolParameter("artifact", ParamType.ArtifactRef, true, "Scanned PDF or image."),
        new ToolParameter("language", ParamType.Text, false, "Tesseract language code, default eng (e.g. eng+hin).", 30),
    ], timeoutSec: 900);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var a = await ArtifactAsync(input["artifact"], ct);
        var lang = Input.OptStr(input, "language") ?? "eng";
        if (!System.Text.RegularExpressions.Regex.IsMatch(lang, "^[a-z_]{3,8}(\\+[a-z_]{3,8})*$"))
            throw new ToolException("language_invalid", "Use a Tesseract language code like eng or eng+hin.", ErrorCategory.Validation);
        var tesseract = EngineLocator.Require("Tesseract");
        var work = ArtifactStore.WorkDir(context.WorkflowId);

        List<string> images;
        if (DocumentConverter.Ext(a.FileName) == "pdf")
        {
            await ProcessRunner.RunAsync(EngineLocator.Require("Ghostscript"),
                ["-sDEVICE=png16m", "-r300", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-dQUIET", $"-sOutputFile={Path.Combine(work, "page-%04d.png")}", a.StagingPath], work, ct);
            images = Directory.GetFiles(work, "page-*.png").Order().ToList();
        }
        else
        {
            images = [a.StagingPath];
        }

        var pagePdfs = new List<string>();
        foreach (var img in images)
        {
            ct.ThrowIfCancellationRequested();
            var outBase = Path.Combine(work, Path.GetFileNameWithoutExtension(img) + "-ocr");
            await ProcessRunner.RunAsync(tesseract, [img, outBase, "-l", lang, "pdf"], work, ct);
            pagePdfs.Add(outBase + ".pdf");
        }
        var output = Path.Combine(work, "ocr.pdf");
        PdfOps.Merge(pagePdfs, output);
        return await StageAsync(context, output, $"{Stem(a)}-searchable.pdf", ct);
    }
}
