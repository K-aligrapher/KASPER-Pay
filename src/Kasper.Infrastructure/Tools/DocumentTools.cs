using System.Text.Json.Nodes;
using Kasper.Application.Planning;
using Kasper.Application.Tools;
using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Documents;
using Kasper.Infrastructure.Files;

namespace Kasper.Infrastructure.Tools;

/// <summary>Shared helpers: resolve input artifacts and stage outputs. Inputs are never modified.</summary>
public abstract class DocumentToolBase(ArtifactStore artifacts)
{
    protected ArtifactStore Artifacts { get; } = artifacts;

    protected async Task<Artifact> ArtifactAsync(JsonNode? node, CancellationToken ct)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s) || !Guid.TryParse(s, out var id) || await Artifacts.FindAsync(id, ct) is not { } a)
            throw new ToolException("artifact_unknown", "An input file was not found.", ErrorCategory.Permanent);
        if (!File.Exists(a.StagingPath))
            throw new ToolException("artifact_missing", $"{a.FileName} is no longer in staging.", ErrorCategory.Permanent);
        return a;
    }

    protected async Task<ToolResult> StageAsync(ToolContext ctx, string producedPath, string fileName, CancellationToken ct, JsonObject? extra = null)
    {
        if (PlanValidator.FileNameProblem(fileName) is not null) fileName = "output" + Path.GetExtension(fileName);
        var a = await Artifacts.CreateFromFileAsync(ctx.WorkflowId, ctx.StepId, producedPath, fileName, ct);
        var output = new JsonObject { ["artifact"] = a.Id.ToString(), ["fileName"] = a.FileName, ["sizeBytes"] = a.SizeBytes };
        if (extra is not null) foreach (var (k, val) in extra) output[k] = val?.DeepClone();
        return new ToolResult(output);
    }

    protected static string Stem(Artifact a) => Path.GetFileNameWithoutExtension(a.FileName);

    protected static ToolDescriptor Pdf(string id, string description, IReadOnlyList<ToolParameter> parameters, int timeoutSec = 120) =>
        new(id, 1, description, EffectClass.LocalWrite, WritesOnlyToStaging: true, parameters,
            ["artifact", "fileName", "sizeBytes"], TimeSpan.FromSeconds(timeoutSec), MaxAttempts: 1, Idempotent: true);
}

/// <summary>files.import: brings a file the user uploaded/dropped into this workflow's staging. Server-made plans only.</summary>
public sealed class ImportFileTool(ArtifactStore artifacts) : DocumentToolBase(artifacts), ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "files.import", 1, "Bring a file you dropped into KASPER into this task.", EffectClass.Read, true,
        [new ToolParameter("upload", ParamType.Text, true, "Upload id.", 64)],
        ["artifact", "fileName", "sizeBytes"], TimeSpan.FromSeconds(60), 1, Idempotent: true, HiddenFromPlanner: true);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        if (!Guid.TryParse(Input.Str(input, "upload"), out var token))
            throw new ToolException("upload_invalid", "Invalid upload id.", ErrorCategory.Validation);
        var dir = ArtifactStore.UploadDir(token);
        var file = Directory.Exists(dir) ? Directory.GetFiles(dir).SingleOrDefault() : null;
        if (file is null)
        {
            // Replay after restart: the file was already imported by this step.
            if (await Artifacts.FindByStepAsync(context.StepId, ct) is { } done)
                return new ToolResult(new JsonObject { ["artifact"] = done.Id.ToString(), ["fileName"] = done.FileName, ["sizeBytes"] = done.SizeBytes });
            throw new ToolException("upload_missing", "The dropped file is gone. Please add it again.", ErrorCategory.Permanent);
        }
        var result = await StageAsync(context, file, Path.GetFileName(file), ct);
        Directory.Delete(dir, recursive: true);
        return result;
    }
}

/// <summary>documents.convert: any-to-any via the conversion router.</summary>
public sealed class ConvertTool(ArtifactStore artifacts, DocumentConverter converter) : DocumentToolBase(artifacts), ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "documents.convert", 1, "Convert a document to another format (pdf, docx, odt, rtf, txt, html, md, epub, xlsx, csv, pptx…).",
        EffectClass.LocalWrite, true,
        [
            new ToolParameter("artifact", ParamType.ArtifactRef, true, "The file to convert."),
            new ToolParameter("to", ParamType.Text, true, "Target format extension, e.g. pdf.", 8),
        ],
        ["artifact", "fileName", "sizeBytes", "lossy"], TimeSpan.FromMinutes(5), MaxAttempts: 1, Idempotent: true);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var a = await ArtifactAsync(input["artifact"], ct);
        var to = Input.Str(input, "to").TrimStart('.').ToLowerInvariant();
        var work = ArtifactStore.WorkDir(context.WorkflowId);
        var output = await converter.ConvertAsync(a.StagingPath, to, work, ct);
        var lossy = DocumentConverter.IsLossy(DocumentConverter.Ext(a.FileName), to);
        return await StageAsync(context, output, $"{Stem(a)}.{to}", ct, new JsonObject { ["lossy"] = lossy });
    }
}

public sealed class PdfMergeTool(ArtifactStore artifacts) : DocumentToolBase(artifacts), ITool
{
    public ToolDescriptor Descriptor { get; } = Pdf("pdf.merge", "Merge several PDFs into one, in the given order.",
    [
        new ToolParameter("artifacts", ParamType.ArtifactList, true, "PDFs to merge, in order."),
        new ToolParameter("fileName", ParamType.FileName, false, "Name for the merged PDF.", 120),
    ]);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var list = input["artifacts"] as JsonArray ?? [];
        var files = new List<Artifact>();
        foreach (var n in list) files.Add(await ArtifactAsync(n, ct));
        RequirePdf(files);
        var output = Path.Combine(ArtifactStore.WorkDir(context.WorkflowId), "merged.pdf");
        PdfOps.Merge(files.Select(f => f.StagingPath), output);
        return await StageAsync(context, output, Input.OptStr(input, "fileName") ?? $"{Stem(files[0])}-merged.pdf", ct);
    }

    internal static void RequirePdf(IEnumerable<Artifact> files)
    {
        foreach (var f in files.Where(f => DocumentConverter.Ext(f.FileName) != "pdf"))
            throw new ToolException("not_pdf", $"{f.FileName} is not a PDF. Convert it first.", ErrorCategory.Validation);
    }
}

public sealed class PdfExtractTool(ArtifactStore artifacts) : DocumentToolBase(artifacts), ITool
{
    public ToolDescriptor Descriptor { get; } = Pdf("pdf.extract_pages", "Keep only some pages of a PDF (split / extract / delete / reorder).",
    [
        new ToolParameter("artifact", ParamType.ArtifactRef, true, "The PDF."),
        new ToolParameter("pages", ParamType.Text, true, "Pages to keep in order, e.g. 1-3,5.", 200),
    ]);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var a = await ArtifactAsync(input["artifact"], ct);
        PdfMergeTool.RequirePdf([a]);
        var range = Input.Str(input, "pages");
        var pages = PdfOps.ParseRange(range, PdfOps.PageCount(a.StagingPath));
        var output = Path.Combine(ArtifactStore.WorkDir(context.WorkflowId), "pages.pdf");
        PdfOps.Extract(a.StagingPath, pages, output);
        return await StageAsync(context, output, $"{Stem(a)}-pages-{range.Replace(",", "_")}.pdf", ct);
    }
}

public sealed class PdfRotateTool(ArtifactStore artifacts) : DocumentToolBase(artifacts), ITool
{
    public ToolDescriptor Descriptor { get; } = Pdf("pdf.rotate", "Rotate pages of a PDF by 90, 180 or 270 degrees.",
    [
        new ToolParameter("artifact", ParamType.ArtifactRef, true, "The PDF."),
        new ToolParameter("degrees", ParamType.Integer, true, "90, 180 or 270."),
        new ToolParameter("pages", ParamType.Text, false, "Pages to rotate (default all), e.g. 2,4.", 200),
    ]);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var a = await ArtifactAsync(input["artifact"], ct);
        PdfMergeTool.RequirePdf([a]);
        var degrees = Input.Int(input, "degrees", 90);
        if (degrees is not (90 or 180 or 270 or -90))
            throw new ToolException("degrees_invalid", "Rotate by 90, 180 or 270 degrees.", ErrorCategory.Validation);
        var pagesText = Input.OptStr(input, "pages");
        var pages = pagesText is null ? null : PdfOps.ParseRange(pagesText, PdfOps.PageCount(a.StagingPath));
        var output = Path.Combine(ArtifactStore.WorkDir(context.WorkflowId), "rotated.pdf");
        PdfOps.Rotate(a.StagingPath, pages, degrees, output);
        return await StageAsync(context, output, $"{Stem(a)}-rotated.pdf", ct);
    }
}

public sealed class PdfCompressTool(ArtifactStore artifacts) : DocumentToolBase(artifacts), ITool
{
    public ToolDescriptor Descriptor { get; } = Pdf("pdf.compress", "Make a PDF smaller (quality: high, medium or low).",
    [
        new ToolParameter("artifact", ParamType.ArtifactRef, true, "The PDF."),
        new ToolParameter("quality", ParamType.Text, false, "high, medium (default) or low.", 10),
    ], 300);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var a = await ArtifactAsync(input["artifact"], ct);
        PdfMergeTool.RequirePdf([a]);
        var work = ArtifactStore.WorkDir(context.WorkflowId);
        var output = Path.Combine(work, "compressed.pdf");
        var preset = (Input.OptStr(input, "quality") ?? "medium") switch
        {
            "high" => "/printer",
            "low" => "/screen",
            _ => "/ebook",
        };
        var gs = EngineLocator.Find("Ghostscript");
        string method;
        if (gs is not null)
        {
            await ProcessRunner.RunAsync(gs,
                ["-sDEVICE=pdfwrite", $"-dPDFSETTINGS={preset}", "-dCompatibilityLevel=1.5", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-dQUIET", $"-sOutputFile={output}", a.StagingPath],
                work, ct);
            method = "ghostscript";
        }
        else
        {
            PdfOps.CompressLossless(a.StagingPath, output);
            method = "lossless";
        }
        // Never hand back a bigger file.
        if (new FileInfo(output).Length >= a.SizeBytes) File.Copy(a.StagingPath, output, overwrite: true);
        var saved = a.SizeBytes - new FileInfo(output).Length;
        return await StageAsync(context, output, $"{Stem(a)}-compressed.pdf", ct,
            new JsonObject { ["method"] = method, ["savedBytes"] = saved });
    }
}

public sealed class ImagesToPdfTool(ArtifactStore artifacts) : DocumentToolBase(artifacts), ITool
{
    public ToolDescriptor Descriptor { get; } = Pdf("pdf.from_images", "Combine images (PNG, JPG, GIF, BMP) into one PDF, one image per page.",
    [
        new ToolParameter("artifacts", ParamType.ArtifactList, true, "Images in page order."),
        new ToolParameter("fileName", ParamType.FileName, false, "Name for the PDF.", 120),
    ]);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var files = new List<Artifact>();
        foreach (var n in input["artifacts"] as JsonArray ?? []) files.Add(await ArtifactAsync(n, ct));
        var output = Path.Combine(ArtifactStore.WorkDir(context.WorkflowId), "images.pdf");
        PdfOps.ImagesToPdf(files.Select(f => f.StagingPath), output);
        return await StageAsync(context, output, Input.OptStr(input, "fileName") ?? $"{Stem(files[0])}.pdf", ct);
    }
}
