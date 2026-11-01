using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Kasper.Infrastructure.Documents;

/// <summary>
/// Any-to-any conversion (PRD §9) by routing through installed engines. Each engine offers
/// direct edges; the router finds the shortest path, preferring lossless edges.
/// </summary>
public sealed class DocumentConverter
{
    private sealed record Edge(string From, string To, string Engine, bool Lossy);

    private static readonly string[] LoText = ["doc", "docx", "odt", "rtf", "txt", "html", "htm"];
    private static readonly string[] LoSheets = ["xls", "xlsx", "ods", "csv"];
    private static readonly string[] LoSlides = ["ppt", "pptx", "odp"];
    private static readonly string[] PandocIn = ["md", "html", "htm", "docx", "odt", "epub", "rtf", "txt"];
    private static readonly string[] PandocOut = ["md", "html", "docx", "odt", "epub", "rtf", "txt"];
    private static readonly string[] Images = ["png", "jpg", "jpeg", "gif", "bmp"];

    private static readonly List<Edge> AllEdges = BuildEdges();

    private static List<Edge> BuildEdges()
    {
        var e = new List<Edge>();
        foreach (var f in LoText) foreach (var t in new[] { "pdf", "docx", "odt", "rtf", "txt", "html" }) if (f != t) e.Add(new(f, t, "LibreOffice", t == "txt"));
        foreach (var f in LoSheets) foreach (var t in new[] { "pdf", "xlsx", "ods", "csv", "html" }) if (f != t) e.Add(new(f, t, "LibreOffice", t is "csv"));
        foreach (var f in LoSlides) foreach (var t in new[] { "pdf", "pptx", "odp" }) if (f != t) e.Add(new(f, t, "LibreOffice", false));
        e.Add(new("pdf", "docx", "LibreOffice", true)); // PDF import is inherently approximate
        foreach (var f in PandocIn) foreach (var t in PandocOut) if (f != t && !(f is "html" or "htm" && t == "html")) e.Add(new(f, t, "Pandoc", t == "txt"));
        foreach (var f in Images) e.Add(new(f, "pdf", "PDFsharp", false));
        return e;
    }

    private static bool Installed(string engine) => engine == "PDFsharp" || EngineLocator.Find(engine) is not null;

    public static string Ext(string fileName) => Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();

    /// <summary>Targets reachable from a format with the engines installed now.</summary>
    public static IReadOnlyList<string> TargetsFor(string from)
    {
        var edges = AllEdges.Where(x => Installed(x.Engine)).ToList();
        var seen = new HashSet<string> { from };
        var queue = new Queue<(string F, int Depth)>([(from, 0)]);
        while (queue.Count > 0)
        {
            var (f, d) = queue.Dequeue();
            if (d == 3) continue;
            foreach (var x in edges.Where(x => x.From == f && seen.Add(x.To))) queue.Enqueue((x.To, d + 1));
        }
        seen.Remove(from);
        return seen.Where(t => t != "htm").Order().ToList();
    }

    /// <summary>Shortest route (fewest hops, then fewest lossy hops).</summary>
    private static List<Edge>? Route(string from, string to)
    {
        var edges = AllEdges.Where(x => Installed(x.Engine)).ToList();
        var best = new Dictionary<string, (int Hops, int Lossy, List<Edge> Path)> { [from] = (0, 0, []) };
        var queue = new Queue<string>([from]);
        while (queue.Count > 0)
        {
            var f = queue.Dequeue();
            var cur = best[f];
            if (cur.Hops == 3) continue;
            foreach (var x in edges.Where(x => x.From == f))
            {
                var cand = (cur.Hops + 1, cur.Lossy + (x.Lossy ? 1 : 0), new List<Edge>(cur.Path) { x });
                if (!best.TryGetValue(x.To, out var old) || cand.Item1 < old.Hops || (cand.Item1 == old.Hops && cand.Item2 < old.Lossy))
                {
                    best[x.To] = cand;
                    queue.Enqueue(x.To);
                }
            }
        }
        return best.TryGetValue(to, out var r) ? r.Path : null;
    }

    public static bool IsLossy(string from, string to) => Route(from, to)?.Any(e => e.Lossy) ?? false;

    /// <summary>Converts <paramref name="input"/> to <paramref name="to"/>; returns the output path inside <paramref name="workDir"/>.</summary>
    public async Task<string> ConvertAsync(string input, string to, string workDir, CancellationToken ct)
    {
        var from = Ext(input);
        to = to.TrimStart('.').ToLowerInvariant();
        if (from == to) throw new ToolException("same_format", $"The file is already .{to}.", ErrorCategory.Validation);

        var route = Route(from, to);
        if (route is null)
        {
            var missing = AllEdges.Where(e => e.From == from && !Installed(e.Engine)).Select(e => e.Engine).Distinct().ToList();
            var hint = missing.Count > 0 ? $" Installing {string.Join(" or ", missing)} may help: {string.Join(" / ", missing.Select(m => EngineLocator.InstallHints.GetValueOrDefault(m, "")))}" : "";
            throw new ToolException(missing.Count > 0 ? "engine_missing" : "unsupported",
                $"KASPER can't convert .{from} to .{to}.{hint}", missing.Count > 0 ? ErrorCategory.EngineMissing : ErrorCategory.Validation);
        }

        var current = input;
        for (var i = 0; i < route.Count; i++)
        {
            var hopDir = Path.Combine(workDir, $"hop{i}");
            Directory.CreateDirectory(hopDir);
            current = await RunEdgeAsync(route[i], current, hopDir, ct);
        }
        return current;
    }

    private static async Task<string> RunEdgeAsync(Edge e, string input, string outDir, CancellationToken ct)
    {
        var name = Path.GetFileNameWithoutExtension(input);
        var output = Path.Combine(outDir, $"{name}.{e.To}");
        switch (e.Engine)
        {
            case "LibreOffice":
                var profile = new Uri(Path.Combine(outDir, "lo-profile")).AbsoluteUri;
                var args = new List<string> { "--headless", "--norestore", "--nolockcheck", $"-env:UserInstallation={profile}" };
                if (e.From == "pdf") args.Add("--infilter=writer_pdf_import");
                args.AddRange(["--convert-to", e.To, "--outdir", outDir, input]);
                await ProcessRunner.RunAsync(EngineLocator.Require("LibreOffice"), args, outDir, ct);
                break;
            case "Pandoc":
                var fmt = e.To switch { "txt" => "plain", "md" => "gfm", _ => e.To };
                var inFmt = e.From switch { "txt" => "markdown", "md" => "gfm", "htm" => "html", _ => e.From };
                await ProcessRunner.RunAsync(EngineLocator.Require("Pandoc"),
                    ["-f", inFmt, "-t", fmt, "-s", "-o", output, input], outDir, ct);
                break;
            case "PDFsharp":
                PdfOps.ImagesToPdf([input], output);
                break;
        }
        if (!File.Exists(output))
            throw new ToolException("engine_no_output", $"{e.Engine} produced no .{e.To} file.", ErrorCategory.Permanent);
        return output;
    }
}

/// <summary>Page-level PDF operations with PDFsharp (MIT), all in-process and lossless.</summary>
public static class PdfOps
{
    private static PdfDocument OpenImport(string path)
    {
        try { return PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ToolException("pdf_unreadable", $"{Path.GetFileName(path)} could not be read as a PDF (it may be damaged or password-protected).", ErrorCategory.Permanent, ex);
        }
    }

    public static int PageCount(string path)
    {
        using var d = OpenImport(path);
        return d.PageCount;
    }

    public static void Merge(IEnumerable<string> inputs, string output)
    {
        using var result = new PdfDocument();
        foreach (var input in inputs)
        {
            using var src = OpenImport(input);
            foreach (var page in src.Pages) result.AddPage(page);
        }
        result.Save(output);
    }

    /// <summary>Keeps the given 1-based pages in that order.</summary>
    public static void Extract(string input, IReadOnlyList<int> pages, string output)
    {
        using var src = OpenImport(input);
        using var result = new PdfDocument();
        foreach (var p in pages) result.AddPage(src.Pages[p - 1]);
        result.Save(output);
    }

    public static void Rotate(string input, IReadOnlyList<int>? pages, int degrees, string output)
    {
        using var src = OpenImport(input);
        using var result = new PdfDocument();
        for (var i = 0; i < src.PageCount; i++)
        {
            var page = result.AddPage(src.Pages[i]);
            if (pages is null || pages.Contains(i + 1)) page.Rotate = ((page.Rotate + degrees) % 360 + 360) % 360;
        }
        result.Save(output);
    }

    /// <summary>Lossless re-save with compressed streams (fallback when Ghostscript is missing).</summary>
    public static void CompressLossless(string input, string output)
    {
        using var src = OpenImport(input);
        using var result = new PdfDocument();
        foreach (var page in src.Pages) result.AddPage(page);
        result.Options.CompressContentStreams = true;
        result.Options.FlateEncodeMode = PdfSharp.Pdf.PdfFlateEncodeMode.BestCompression;
        result.Options.NoCompression = false;
        result.Save(output);
    }

    public static void ImagesToPdf(IEnumerable<string> images, string output)
    {
        using var doc = new PdfDocument();
        foreach (var img in images)
        {
            XImage image;
            try { image = XImage.FromFile(img); }
            catch (Exception ex)
            {
                throw new ToolException("image_unreadable", $"{Path.GetFileName(img)} is not a supported image (use PNG, JPG, GIF or BMP).", ErrorCategory.Permanent, ex);
            }
            using (image)
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(image.PointWidth);
                page.Height = XUnit.FromPoint(image.PointHeight);
                using var g = XGraphics.FromPdfPage(page);
                g.DrawImage(image, 0, 0, image.PointWidth, image.PointHeight);
            }
        }
        doc.Save(output);
    }

    /// <summary>Parses "1-3,5,8-" style ranges against a page count.</summary>
    public static List<int> ParseRange(string range, int pageCount)
    {
        var pages = new List<int>();
        foreach (var part in range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split('-', StringSplitOptions.TrimEntries);
            if (bits.Length > 2 || !int.TryParse(bits[0] == "" ? "1" : bits[0], out var a))
                throw new ToolException("range_invalid", $"'{part}' is not a page range. Use e.g. 1-3,5.", ErrorCategory.Validation);
            var b = a;
            if (bits.Length == 2 && !int.TryParse(bits[1] == "" ? pageCount.ToString() : bits[1], out b))
                throw new ToolException("range_invalid", $"'{part}' is not a page range.", ErrorCategory.Validation);
            if (a < 1 || b > pageCount || a > b)
                throw new ToolException("range_invalid", $"Pages {part} are outside 1–{pageCount}.", ErrorCategory.Validation);
            for (var i = a; i <= b; i++) pages.Add(i);
        }
        if (pages.Count == 0) throw new ToolException("range_invalid", "No pages selected.", ErrorCategory.Validation);
        return pages;
    }
}
