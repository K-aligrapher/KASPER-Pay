using Spectre.Console;

namespace Kasper.Cli;

/// <summary>Draws the pixel bunny in the terminal from the shared grid (PRD §24.3).</summary>
internal static class Banner
{
    public const string Gold = "#F5B800";
    private static readonly Dictionary<char, string> Colors = new()
    {
        ['B'] = "#0D0D0D", ['G'] = Gold, ['D'] = "#B37F00", ['W'] = "#FFF6D6",
    };

    private static readonly string[] Grid = LoadGrid();

    private static string[] LoadGrid()
    {
        using var stream = typeof(Banner).Assembly.GetManifestResourceStream("kasper-logo.grid.txt")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();
    }

    /// <summary>Bunny as markup lines: two pixel rows per line using half blocks.</summary>
    public static IReadOnlyList<string> BunnyLines()
    {
        if (!AnsiConsole.Profile.Capabilities.Ansi || AnsiConsole.Profile.Capabilities.ColorSystem < ColorSystem.TrueColor)
            return PlainBunny;

        var lines = new List<string>();
        for (var y = 0; y < Grid.Length; y += 2)
        {
            var sb = new System.Text.StringBuilder();
            for (var x = 0; x < Grid[y].Length; x++)
            {
                Colors.TryGetValue(Grid[y][x], out var top);
                string? bottom = null;
                if (y + 1 < Grid.Length) Colors.TryGetValue(Grid[y + 1][x], out bottom);

                sb.Append((top, bottom) switch
                {
                    (null, null) => " ",
                    ({ } t, { } b) => $"[{t} on {b}]▀[/]",
                    ({ } t, null) => $"[{t}]▀[/]",
                    (null, { } b) => $"[{b}]▄[/]",
                });
            }
            lines.Add(sb.ToString());
        }
        return lines;
    }

    private static readonly string[] PlainBunny =
    [
        @"  (\ /)  ",
        @"  ( . .) ",
        @"  c("")("")",
    ];

    public static void Print(string version, string statusMarkup)
    {
        var bunny = BunnyLines();
        var right = new[]
        {
            "",
            $"[bold {Gold}]K A S P E R[/]  [grey]v{version}[/]",
            "[grey]your desktop coworker[/]",
            "",
            statusMarkup,
            "",
            "[grey]Type a request, [/][" + Gold + "]/help[/][grey] for commands, [/][" + Gold + "]exit[/][grey] to leave.[/]",
        };

        AnsiConsole.WriteLine();
        for (var i = 0; i < Math.Max(bunny.Count, right.Length); i++)
        {
            var left = i < bunny.Count ? bunny[i] : new string(' ', 16);
            var text = i < right.Length ? right[i] : "";
            AnsiConsole.MarkupLine($"  {left}   {text}");
        }
        AnsiConsole.WriteLine();
    }
}
