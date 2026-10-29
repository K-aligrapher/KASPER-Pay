using System.Text;
using Kasper.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

Console.OutputEncoding = Encoding.UTF8;
if (Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 })
    AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
else if (AnsiConsole.Profile.Capabilities.Ansi && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
    // Classic cmd/conhost supports 24-bit colour on Windows 10 1703+, but is often detected as 8-bit.
    AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.TrueColor;

var app = new CommandApp<InteractiveCommand>();
app.Configure(config =>
{
    config.SetApplicationName("kasper");
    config.SetApplicationVersion(Ui.Version);

    config.AddCommand<AskCommand>("ask")
        .WithDescription("Ask KASPER something or give it a task.")
        .WithExample("ask", "\"summarise what a PRD is in two lines\"")
        .WithExample("ask", "\"write a packing list for a 3-day trip and save it as packing.md\"");
    config.AddCommand<ApprovalsCommand>("approvals").WithDescription("List actions waiting for your approval.");
    config.AddCommand<ReviewCommand>("review").WithDescription("Approve, edit or reject waiting actions (Final Review).");
    config.AddCommand<StatusCommand>("status").WithDescription("Recent tasks, or one task's full trace.");
    config.AddCommand<CancelCommand>("cancel").WithDescription("Cancel a task.");
    config.AddCommand<OpenCommand>("open").WithDescription("Open the KASPER desktop window.");
    config.AddCommand<Doctor>("doctor").WithDescription("Check Ollama, the database and document engines.");

    config.SetExceptionHandler((ex, _) =>
    {
        switch (ex)
        {
            case KasperCliException k:
                Ui.Error(k.Message);
                return 1;
            case OperationCanceledException:
                return 130;
            case CommandAppException c:
                Ui.Error(c.Message);
                return 2;
            default:
                Ui.Error($"Unexpected error: {ex.Message}");
                return 1;
        }
    });
});

return await app.RunAsync(args);
