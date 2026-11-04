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

/// <summary>`kasper drop <files>`: opens the files in the Documents screen (used by Explorer "Send to → KASPER").</summary>
internal sealed class DropCommand : AsyncCommand<DropCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<files>")]
        public string[] Files { get; init; } = [];
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var paths = s.Files.Select(Path.GetFullPath).Where(File.Exists).Take(30).ToList();
        if (paths.Count == 0) throw new KasperCliException("No files to open.");
        using var client = await KasperClient.ConnectAsync(true, ct);
        var uploads = await client.UploadAsync(paths, ct);
        await client.ShowWindowAsync(ct, "documents/" + string.Join(",", uploads.Select(u => u.ToString("N"))));
        return 0;
    }
}

/// <summary>`kasper projects` and `kasper project <name> [briefing]`.</summary>
internal sealed class ProjectsCommand : AsyncCommand<ProjectsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[name]")]
        public string? Name { get; init; }

        [CommandArgument(1, "[action]")]
        [Description("briefing")]
        public string? Action { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        using var client = await KasperClient.ConnectAsync(true, ct);
        var projects = await client.ProjectsAsync(ct);
        if (s.Name is null)
        {
            if (projects.Count == 0) { AnsiConsole.MarkupLine("[grey]No projects yet. Create one in the Projects screen (kasper open).[/]"); return 0; }
            var t = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumn("Project").AddColumn("Status").AddColumn("GitHub").AddColumn("Slack").AddColumn("Folder");
            foreach (var p in projects)
                t.AddRow($"[{Banner.Gold}]{Markup.Escape(p.Name)}[/]", p.Status, Markup.Escape(p.GitHubRepo ?? "—"), Markup.Escape(p.SlackChannel ?? "—"), Markup.Escape(p.FolderPath ?? "—"));
            AnsiConsole.Write(t);
            return 0;
        }
        var match = projects.FirstOrDefault(p => p.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))
            ?? projects.FirstOrDefault(p => p.Name.Contains(s.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KasperCliException($"No project matches {s.Name}.");
        var result = s.Action?.ToLowerInvariant() == "briefing"
            ? await Ui.Thinking(() => client.BriefingAsync(match.Id, ct))
            : match;
        AnsiConsole.MarkupLine($"[bold {Banner.Gold}]{Markup.Escape(result.Name)}[/] [grey]{Markup.Escape(result.Description)}[/]");
        AnsiConsole.WriteLine(result.LastBriefing ?? "No briefing yet. Run: kasper project \"" + result.Name + "\" briefing");
        return 0;
    }
}
