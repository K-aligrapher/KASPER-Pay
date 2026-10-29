using System.ComponentModel;
using Kasper.Application.Contracts;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kasper.Cli;

/// <summary>Following workflows and running Final Review in the terminal (FR-505).</summary>
internal static class Flow
{
    private static readonly HashSet<string> Settled = ["WaitingForApproval", "Succeeded", "Failed", "Cancelled"];

    public static string Short(Guid id) => id.ToString("N")[..8];

    public static string StatusMarkup(string status) => status switch
    {
        "Pending" => "[blue]queued[/]",
        "Running" => "[blue]working[/]",
        "WaitingForApproval" => $"[black on {Banner.Gold}] needs your approval [/]",
        "Succeeded" => "[green]done[/]",
        "Failed" => "[red]failed[/]",
        "Cancelled" => "[grey]cancelled[/]",
        "Approved" => "[green]approved[/]",
        "Rejected" => "[red]rejected[/]",
        "Invalidated" => "[grey]replaced[/]",
        _ => Markup.Escape(status),
    };

    /// <summary>Shows live progress until the workflow finishes, reviewing approvals inline when possible.</summary>
    public static async Task FollowAsync(KasperClient client, Guid id, CancellationToken ct)
    {
        while (true)
        {
            var detail = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse(Banner.Gold))
                .StartAsync("[grey]working…[/]", async ctx =>
                {
                    while (true)
                    {
                        var d = await client.WorkflowAsync(id, ct);
                        var current = d.Steps.FirstOrDefault(s => s.Status is "Running" or "Pending");
                        if (current is not null) ctx.Status($"[grey]{Markup.Escape(current.Purpose)}…[/]");
                        if (Settled.Contains(d.Workflow.Status)) return d;
                        await Task.Delay(600, ct);
                    }
                });

            switch (detail.Workflow.Status)
            {
                case "WaitingForApproval":
                    var pending = detail.Approvals.Where(a => a.Status == "Pending").ToList();
                    if (pending.Count == 0) { await Task.Delay(500, ct); continue; }
                    if (!await ReviewAsync(client, pending, ct)) return;
                    continue;

                case "Succeeded":
                    AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(detail.Workflow.Summary)}");
                    foreach (var a in detail.Artifacts.Where(a => a.CommittedPath is not null))
                        AnsiConsole.MarkupLine($"  [grey]saved[/] {Markup.Escape(a.CommittedPath!)}");
                    return;

                case "Failed":
                    Ui.Error($"{detail.Workflow.Summary}: {detail.Workflow.FailureSummary}");
                    AnsiConsole.MarkupLine($"  [grey]details: kasper status {Short(id)}[/]");
                    return;

                default:
                    AnsiConsole.MarkupLine($"[grey]✗ {Markup.Escape(detail.Workflow.Summary)} — {Markup.Escape(detail.Workflow.FailureSummary ?? "cancelled")}[/]");
                    return;
            }
        }
    }

    /// <summary>
    /// Shows each pending action and asks for a keypress. There is deliberately no flag to skip this.
    /// Returns false when the user chose "later" or the terminal cannot ask.
    /// </summary>
    public static async Task<bool> ReviewAsync(KasperClient client, IReadOnlyList<ApprovalDto> approvals, CancellationToken ct)
    {
        if (Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine($"[{Banner.Gold}]![/] {approvals.Count} action(s) need your approval. Run [bold]kasper review[/] in a terminal or open the Approvals screen.");
            return false;
        }

        foreach (var a in approvals)
        {
            Render(a);
            while (true)
            {
                var hasEdit = a.Editable.Count > 0;
                AnsiConsole.Markup($"[black on {Banner.Gold}] A [/] approve   {(hasEdit ? "[bold] E [/] edit   " : "")}[red] R [/] reject   [grey] L [/] later  › ");
                var key = char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar);
                AnsiConsole.WriteLine();
                try
                {
                    switch (key)
                    {
                        case 'a':
                            await client.DecideAsync(a.Id, new DecisionDto("approve"), ct);
                            AnsiConsole.MarkupLine("[green]✓ approved[/]");
                            break;
                        case 'r':
                            var note = AnsiConsole.Prompt(new TextPrompt<string>("[grey]reason (optional):[/]").AllowEmpty());
                            await client.DecideAsync(a.Id, new DecisionDto("reject", Note: note), ct);
                            AnsiConsole.MarkupLine("[red]✗ rejected[/] [grey]— nothing was saved or sent.[/]");
                            return true;
                        case 'e' when hasEdit:
                            var edits = AskEdits(a);
                            if (edits.Count == 0) continue;
                            await client.DecideAsync(a.Id, new DecisionDto("edit", edits), ct);
                            AnsiConsole.MarkupLine("[grey]updated — you'll review the new version next.[/]");
                            return true; // the engine creates a fresh review; FollowAsync picks it up
                        case 'l':
                            AnsiConsole.MarkupLine($"[grey]ok — it waits in Approvals. Later: kasper review {Short(a.WorkflowId)}[/]");
                            return false;
                        default:
                            continue;
                    }
                }
                catch (KasperCliException ex)
                {
                    Ui.Error(ex.Message);
                    continue;
                }
                break;
            }
        }
        return true;
    }

    private static Dictionary<string, string> AskEdits(ApprovalDto a)
    {
        var edits = new Dictionary<string, string>();
        foreach (var f in a.Editable)
        {
            string value;
            if (f.Options is { Count: > 0 } options)
            {
                var choice = AnsiConsole.Prompt(new SelectionPrompt<OptionDto>()
                    .Title($"{Markup.Escape(f.Name)} [grey]({Markup.Escape(f.Description)})[/]")
                    .UseConverter(o => Markup.Escape(o.Label) + (o.Value == f.Value ? " [grey](current)[/]" : ""))
                    .AddChoices(options));
                value = choice.Value;
            }
            else
            {
                value = AnsiConsole.Prompt(new TextPrompt<string>($"{Markup.Escape(f.Name)} [grey]({Markup.Escape(f.Description)})[/]")
                    .DefaultValue(f.Value ?? "").AllowEmpty());
            }
            if (value != (f.Value ?? "")) edits[f.Name] = value;
        }
        return edits;
    }

    public static void Render(ApprovalDto a)
    {
        var rows = new List<Spectre.Console.Rendering.IRenderable>
        {
            new Markup($"[bold]{Markup.Escape(a.Title)}[/]"),
            new Markup($"[grey]{Markup.Escape(a.Target)}[/]"),
        };
        if (a.Fields.Count > 0)
        {
            var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
            foreach (var f in a.Fields) grid.AddRow($"[grey]{Markup.Escape(f.Label)}[/]", Markup.Escape(f.Value));
            rows.Add(grid);
        }
        foreach (var w in a.Warnings) rows.Add(new Markup($"[#FF8A3D]⚠ {Markup.Escape(w)}[/]"));
        if (a.Content is { Length: > 0 } content)
        {
            var lines = content.Split('\n');
            var shown = string.Join('\n', lines.Take(20)) + (lines.Length > 20 ? $"\n… ({lines.Length - 20} more lines)" : "");
            rows.Add(new Panel(new Text(shown)).Border(BoxBorder.Rounded).BorderColor(Color.Grey).Header("[grey]content[/]"));
        }

        AnsiConsole.Write(new Panel(new Rows(rows))
            .Header($"[black on {Banner.Gold}] FINAL REVIEW [/] [grey]{Markup.Escape(a.WorkflowSummary)}[/]")
            .Border(BoxBorder.Heavy).BorderColor(new Color(0xF5, 0xB8, 0x00)).Expand());
    }

    /// <summary>Finds a workflow by full id or the 8-character short id shown in lists.</summary>
    public static async Task<Guid> ResolveWorkflowIdAsync(KasperClient client, string text, CancellationToken ct)
    {
        if (Guid.TryParse(text, out var full)) return full;
        var matches = (await client.WorkflowsAsync(200, ct)).Where(w => w.Id.ToString("N").StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0].Id,
            0 => throw new KasperCliException($"No workflow starts with '{text}'. Run `kasper status` to list them."),
            _ => throw new KasperCliException($"'{text}' matches several workflows; type more characters."),
        };
    }
}

/// <summary>`kasper approvals`: list everything waiting for Final Review.</summary>
internal sealed class ApprovalsCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellation)
    {
        using var client = await KasperClient.ConnectAsync(true, cancellation);
        var approvals = await client.ApprovalsAsync(cancellation);
        if (approvals.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✓[/] Nothing is waiting for your approval.");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
            .AddColumn("Workflow").AddColumn("Task").AddColumn("Action").AddColumn("Target");
        foreach (var a in approvals)
            table.AddRow($"[{Banner.Gold}]{Flow.Short(a.WorkflowId)}[/]", Markup.Escape(a.WorkflowSummary), Markup.Escape(a.Title), $"[grey]{Markup.Escape(a.Target)}[/]");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Review them with:[/] kasper review [grey](or kasper review <workflow>)[/]");
        return 0;
    }
}

/// <summary>`kasper review [workflow]`: approve, edit or reject pending actions.</summary>
internal sealed class ReviewCommand : AsyncCommand<ReviewCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[workflow]")]
        [Description("Workflow id (the 8 characters shown in lists). Omit to review everything waiting.")]
        public string? Workflow { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        using var client = await KasperClient.ConnectAsync(true, cancellation);
        var approvals = await client.ApprovalsAsync(cancellation);
        if (settings.Workflow is { } w)
        {
            var id = await Flow.ResolveWorkflowIdAsync(client, w, cancellation);
            approvals = approvals.Where(a => a.WorkflowId == id).ToList();
        }
        if (approvals.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✓[/] Nothing is waiting for your approval.");
            return 0;
        }
        foreach (var group in approvals.GroupBy(a => a.WorkflowId))
        {
            if (!await Flow.ReviewAsync(client, group.ToList(), cancellation)) return 0;
            await Flow.FollowAsync(client, group.Key, cancellation);
        }
        return 0;
    }
}

/// <summary>`kasper status [workflow]`: recent workflows, or one workflow's trace.</summary>
internal sealed class StatusCommand : AsyncCommand<StatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[workflow]")]
        [Description("Workflow id to show in detail.")]
        public string? Workflow { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        using var client = await KasperClient.ConnectAsync(true, cancellation);
        if (settings.Workflow is null)
        {
            var list = await client.WorkflowsAsync(15, cancellation);
            if (list.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No workflows yet. Try:[/] kasper ask \"write a short note about today and save it\"");
                return 0;
            }
            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumn("Id").AddColumn("Task").AddColumn("Status").AddColumn("Started");
            foreach (var w in list)
                table.AddRow($"[{Banner.Gold}]{Flow.Short(w.Id)}[/]", Markup.Escape(w.Summary), Flow.StatusMarkup(w.Status), w.CreatedAtUtc.ToLocalTime().ToString("g"));
            AnsiConsole.Write(table);
            return 0;
        }

        var d = await client.WorkflowAsync(await Flow.ResolveWorkflowIdAsync(client, settings.Workflow, cancellation), cancellation);
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(d.Workflow.Summary)}[/]  {Flow.StatusMarkup(d.Workflow.Status)}");
        AnsiConsole.MarkupLine($"[grey]“{Markup.Escape(d.Workflow.RequestText)}” · {d.Workflow.CreatedAtUtc.ToLocalTime():g}[/]");
        if (d.Workflow.FailureSummary is { } f) AnsiConsole.MarkupLine($"[grey]{Markup.Escape(f)}[/]");

        var steps = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumn("#").AddColumn("Step").AddColumn("Tool").AddColumn("Status").AddColumn("Tries").AddColumn("Error");
        foreach (var s in d.Steps)
            steps.AddRow(s.Sequence.ToString(), Markup.Escape(s.Purpose), $"[grey]{s.ToolId}[/]{(s.RequiresApproval ? $" [{Banner.Gold}]★[/]" : "")}",
                Flow.StatusMarkup(s.Status), s.AttemptCount.ToString(), Markup.Escape(s.ErrorSummary ?? ""));
        AnsiConsole.Write(steps);

        foreach (var a in d.Approvals)
        {
            var state = a.Status == "Pending" ? $"[{Banner.Gold}]waiting for you[/]" : Flow.StatusMarkup(a.Status);
            AnsiConsole.MarkupLine($"  review: {state} {Markup.Escape(a.Title)} [grey]{Markup.Escape(a.DecidedBy ?? "")}[/]");
        }
        if (d.Workflow.Status == "WaitingForApproval")
            AnsiConsole.MarkupLine($"  [grey]approve with:[/] kasper review {Flow.Short(d.Workflow.Id)}");
        foreach (var a in d.Artifacts.Where(a => a.CommittedPath is not null))
            AnsiConsole.MarkupLine($"  saved: {Markup.Escape(a.CommittedPath!)}");
        return 0;
    }
}

/// <summary>`kasper cancel <workflow>`.</summary>
internal sealed class CancelCommand : AsyncCommand<CancelCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<workflow>")]
        public string Workflow { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        using var client = await KasperClient.ConnectAsync(true, cancellation);
        await client.CancelAsync(await Flow.ResolveWorkflowIdAsync(client, settings.Workflow, cancellation), cancellation);
        AnsiConsole.MarkupLine("[grey]Cancelling… nothing that wasn't approved will be saved or sent.[/]");
        return 0;
    }
}
