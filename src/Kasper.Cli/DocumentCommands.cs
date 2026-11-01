using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kasper.Cli;

internal static class DocJob
{
    public static async Task<int> RunAsync(string[] files, Func<List<Guid>, object> buildJob, CancellationToken ct)
    {
        var paths = files.Select(Path.GetFullPath).ToList();
        foreach (var p in paths.Where(p => !File.Exists(p)))
            throw new KasperCliException($"File not found: {p}");

        using var client = await KasperClient.ConnectAsync(true, ct);
        var uploads = await client.UploadAsync(paths, ct);
        var wf = await client.DocumentJobAsync(buildJob(uploads), ct);
        AnsiConsole.MarkupLine($"[grey]task [{Banner.Gold}]{Flow.Short(wf.Id)}[/]: {Markup.Escape(wf.Summary)}[/]");
        await Flow.FollowAsync(client, wf.Id, ct);
        return 0;
    }
}

/// <summary>`kasper convert a.docx b.docx --to pdf`.</summary>
internal sealed class ConvertCommand : AsyncCommand<ConvertCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<files>")]
        [Description("Files to convert.")]
        public string[] Files { get; init; } = [];

        [CommandOption("-t|--to <FORMAT>")]
        [Description("Target format, e.g. pdf, docx, md, html, txt, epub, xlsx.")]
        public string To { get; init; } = "";

        public override ValidationResult Validate() =>
            string.IsNullOrWhiteSpace(To) ? ValidationResult.Error("Say which format: --to pdf") : ValidationResult.Success();
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct) =>
        DocJob.RunAsync(s.Files, ups => new { operation = "convert", uploads = ups, to = s.To.TrimStart('.') }, ct);
}

/// <summary>`kasper pdf merge|pages|rotate|compress|images ...`.</summary>
internal sealed class PdfCommand : AsyncCommand<PdfCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<operation>")]
        [Description("merge, pages, rotate, compress or images.")]
        public string Operation { get; init; } = "";

        [CommandArgument(1, "<files>")]
        public string[] Files { get; init; } = [];

        [CommandOption("-p|--pages <RANGE>")]
        [Description("Pages, e.g. 1-3,5 (pages: keep these; rotate: only these).")]
        public string? Pages { get; init; }

        [CommandOption("-d|--degrees <DEG>")]
        public int Degrees { get; init; } = 90;

        [CommandOption("-q|--quality <Q>")]
        [Description("high, medium or low (compress).")]
        public string Quality { get; init; } = "medium";

        [CommandOption("-n|--name <FILE>")]
        [Description("Output name for merge/images.")]
        public string? Name { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var op = s.Operation.ToLowerInvariant() switch
        {
            "merge" => "merge",
            "pages" or "extract" or "split" => "extract",
            "rotate" => "rotate",
            "compress" => "compress",
            "images" => "images_to_pdf",
            _ => throw new KasperCliException("Use: kasper pdf merge|pages|rotate|compress|images <files>"),
        };
        if (op == "extract" && string.IsNullOrWhiteSpace(s.Pages)) throw new KasperCliException("Say which pages: --pages 1-3,5");
        return DocJob.RunAsync(s.Files, ups => new
        {
            operation = op, uploads = ups, pages = s.Pages, degrees = s.Degrees, quality = s.Quality, fileName = s.Name,
        }, ct);
    }
}
