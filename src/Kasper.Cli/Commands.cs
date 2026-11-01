using System.ComponentModel;
using System.Reflection;
using Kasper.Application.Contracts;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kasper.Cli;

internal static class Ui
{
    public static string Version { get; } =
        typeof(Ui).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string StatusMarkup(ReadyDto ready) => string.Join("  ",
        ready.Components.Select(c => c.Ok ? $"[green]●[/] {Markup.Escape(c.Name)}" : $"[red]●[/] {Markup.Escape(c.Name)}"));

    public static void Reply(string text) =>
        AnsiConsole.MarkupLine($"[{Banner.Gold}]kasper[/] [grey]›[/] {Markup.Escape(text)}");

    public static void Error(string text) => AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(text)}");

    public static Task<T> Thinking<T>(Func<Task<T>> work) =>
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse(Banner.Gold))
            .StartAsync("[grey]thinking…[/]", _ => work());
}

/// <summary>`kasper` with no arguments: banner + conversation (FR-501).</summary>
internal sealed class InteractiveCommand : AsyncCommand<InteractiveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-logo")]
        [Description("Skip the bunny banner.")]
        public bool NoLogo { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        using var client = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse(Banner.Gold))
            .StartAsync("[grey]waking KASPER up…[/]", _ => KasperClient.ConnectAsync(startIfNeeded: true, cancellation));
        var ready = await client.ReadyAsync(cancellation);

        if (!settings.NoLogo) Banner.Print(Ui.Version, Ui.StatusMarkup(ready));

        var history = new List<ChatMessageDto>();
        while (true)
        {
            AnsiConsole.Markup($"[{Banner.Gold}]kasper ›[/] ");
            var line = Console.ReadLine();
            if (line is null) return 0; // Ctrl+Z / end of input
            line = line.Trim();
            if (line.Length == 0) continue;

            switch (line.ToLowerInvariant())
            {
                case "exit" or "quit" or "/exit" or "/quit":
                    Ui.Reply("See you soon! I'll keep running in the tray.");
                    return 0;
                case "/help" or "help":
                    PrintHelp();
                    continue;
                case "/clear":
                    history.Clear();
                    AnsiConsole.Clear();
                    if (!settings.NoLogo) Banner.Print(Ui.Version, Ui.StatusMarkup(ready));
                    continue;
                case "/open":
                    await client.ShowWindowAsync(cancellation);
                    continue;
                case "/status":
                    ready = await client.ReadyAsync(cancellation);
                    Doctor.PrintComponents(ready);
                    continue;
                case "/approvals" or "/review":
                    var pending = await client.ApprovalsAsync(cancellation);
                    if (pending.Count == 0) AnsiConsole.MarkupLine("[green]✓[/] Nothing is waiting for your approval.");
                    else foreach (var g in pending.GroupBy(a => a.WorkflowId))
                    {
                        if (!await Flow.ReviewAsync(client, g.ToList(), cancellation)) break;
                        await Flow.FollowAsync(client, g.Key, cancellation);
                    }
                    continue;
            }

            history.Add(new ChatMessageDto("user", line));
            try
            {
                var result = await Ui.Thinking(() => client.AssistantAsync(history.TakeLast(12).ToList(), cancellation));
                if (result.Reply is { Length: > 0 } reply)
                {
                    history.Add(new ChatMessageDto("assistant", reply));
                    Ui.Reply(reply);
                }
                if (result.Workflow is { } wf)
                {
                    AnsiConsole.MarkupLine($"[grey]task [{Banner.Gold}]{Flow.Short(wf.Id)}[/]: {Markup.Escape(wf.Summary)}[/]");
                    await Flow.FollowAsync(client, wf.Id, cancellation);
                }
            }
            catch (KasperCliException ex)
            {
                history.RemoveAt(history.Count - 1);
                Ui.Error(ex.Message);
            }
            AnsiConsole.WriteLine();
        }
    }

    private static void PrintHelp()
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumn("Type").AddColumn("Does");
        table.AddRow("[" + Banner.Gold + "]anything[/]", "Ask a question or give a task, e.g. \"write a packing list and save it as packing.md\"");
        table.AddRow("/approvals", "Review everything waiting for your approval");
        table.AddRow("/open", "Open the desktop window");
        table.AddRow("/status", "Show connection status");
        table.AddRow("/clear", "Clear the screen and forget the conversation");
        table.AddRow("exit", "Leave (KASPER keeps running in the tray)");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Also: kasper approvals · kasper review · kasper status · kasper cancel (see `kasper --help`).[/]");
    }
}

/// <summary>`kasper ask "..."`: one-shot request (FR-502).</summary>
internal sealed class AskCommand : AsyncCommand<AskCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<request>")]
        [Description("What you want KASPER to do or answer. Quote it.")]
        public string[] Request { get; init; } = [];

        [CommandOption("--json")]
        [Description("Print the raw JSON response and don't wait for the task.")]
        public bool Json { get; init; }

        [CommandOption("--no-wait")]
        [Description("Start the task and return; review it later with `kasper review`.")]
        public bool NoWait { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var text = string.Join(' ', settings.Request).Trim();
        using var client = await KasperClient.ConnectAsync(startIfNeeded: true, cancellation);
        var result = settings.Json
            ? await client.AssistantAsync([new("user", text)], cancellation)
            : await Ui.Thinking(() => client.AssistantAsync([new("user", text)], cancellation));

        if (settings.Json)
        {
            AnsiConsole.WriteLine(System.Text.Json.JsonSerializer.Serialize(result,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            return 0;
        }

        if (result.Reply is { Length: > 0 } reply) Ui.Reply(reply);
        if (result.Workflow is { } wf)
        {
            AnsiConsole.MarkupLine($"[grey]task [{Banner.Gold}]{Flow.Short(wf.Id)}[/]: {Markup.Escape(wf.Summary)}[/]");
            if (!settings.NoWait) await Flow.FollowAsync(client, wf.Id, cancellation);
        }
        return 0;
    }
}

/// <summary>`kasper open`: start or show the desktop window.</summary>
internal sealed class OpenCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellation)
    {
        using var client = await KasperClient.ConnectAsync(startIfNeeded: true, cancellation);
        await client.ShowWindowAsync(cancellation);
        return 0;
    }
}

/// <summary>`kasper doctor`: check everything KASPER depends on.</summary>
internal sealed class Doctor : AsyncCommand
{
    private static readonly Dictionary<string, string> Purpose = new()
    {
        ["LibreOffice"] = "Office <-> PDF, PDF -> Word",
        ["Pandoc"] = "Markdown, HTML, DOCX, EPUB",
        ["Ghostscript"] = "Strong PDF compression",
        ["QPDF"] = "Lossless PDF optimisation",
        ["Tesseract"] = "OCR (later)",
    };

    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellation)
    {
        AnsiConsole.MarkupLine($"[bold {Banner.Gold}]KASPER doctor[/]  [grey]v{Ui.Version}[/]\n");

        using var client = await KasperClient.ConnectAsync(startIfNeeded: true, cancellation);
        AnsiConsole.MarkupLine($"[green]●[/] KASPER app running [grey](pid {client.Session.ProcessId}, {client.Session.BaseUrl})[/]");
        PrintComponents(await client.ReadyAsync(cancellation));

        // Ask the app itself, so the CLI and the app always agree on what is installed.
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Document engines[/]");
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
            .AddColumn("").AddColumn("Engine").AddColumn("Used for").AddColumn("Install");
        foreach (var e in await client.EnginesAsync(cancellation))
            table.AddRow(e.Installed ? "[green]✓[/]" : "[red]✗[/]", e.Name, Purpose.GetValueOrDefault(e.Name, ""),
                e.Installed ? "[grey]installed[/]" : $"[{Banner.Gold}]{Markup.Escape(e.Install)}[/]");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Merge, pages, rotate, compress and images -> PDF work without any of these.[/]");
        return 0;
    }

    internal static void PrintComponents(ReadyDto ready)
    {
        foreach (var c in ready.Components)
            AnsiConsole.MarkupLine($"{(c.Ok ? "[green]●[/]" : "[red]●[/]")} {Markup.Escape(c.Name)} [grey]— {Markup.Escape(c.Detail)}[/]");
    }
}
