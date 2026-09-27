using System.Text;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Prompt;

/// <summary><c>pk theme list|set &lt;name&gt;|auto [light dark]|show [name]|preview|import</c>.</summary>
public sealed class ThemeCommand(PromptEngine engine) : IPickleCommand
{
    public string Name => "theme";

    public string Description => "List, switch, inspect and preview color themes";

    public string Usage =>
        "pk theme list | set <name|auto> | auto [<light> <dark>] | show [name] | preview [names…]\n"
        + "       pk theme import <scheme name | file.json> [--name <theme>] [--layout <theme>] [--scheme <name>] [--force] | import --list";

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var sub = args.Count == 0 ? "show" : args[0].ToLowerInvariant();
        var code = sub switch
        {
            "list" or "ls" => List(context),
            "set" or "use" => Set(context, args),
            "auto" => Auto(context, args),
            "import" => Import(context, args.Skip(1).ToList()),
            "show" => Show(context, args.Count > 1 ? args[1] : null),
            "preview" => Preview(context, args.Skip(1).ToList()),
            "help" or "-h" or "--help" => Help(context),
            _ => Unknown(context, args[0]),
        };
        return ValueTask.FromResult(code);
    }

    private int List(PickleCommandContext context)
    {
        var themes = context.Pickle.Themes;
        var ui = themes.Current.Ui;
        var names = themes.Available;
        var width = names.Count == 0 ? 8 : names.Max(n => n.Length) + 2;
        foreach (var name in names)
        {
            var current = name.Equals(themes.Current.Name, StringComparison.OrdinalIgnoreCase);
            var description = themes.Load(name)?.Description ?? string.Empty;
            context.WriteHost(
                (current ? Ansi.Colorize("● ", ui.Accent) : "  ")
                + Ansi.Colorize(name.PadRight(width), current ? ui.Accent : null, bold: current)
                + Ansi.Colorize(description, ui.Muted));
        }

        if (themes is ThemeProvider provider)
        {
            var auto = provider.FollowsSystem;
            context.WriteHost(
                (auto ? Ansi.Colorize("● ", ui.Accent) : "  ")
                + Ansi.Colorize(ThemeProvider.Auto.PadRight(width), auto ? ui.Accent : null, bold: auto)
                + Ansi.Colorize(AutoDescription(context, provider), ui.Muted));
        }

        return 0;
    }

    private int Auto(PickleCommandContext context, IReadOnlyList<string> args)
    {
        var themes = context.Pickle.Themes;
        if (args.Count is not (1 or 3))
        {
            context.WriteError("Usage: pk theme auto [<light theme> <dark theme>]");
            return 2;
        }

        if (args.Count == 3)
        {
            foreach (var name in args.Skip(1))
            {
                if (ThemeProvider.IsAuto(name) || themes.Load(name) is null)
                {
                    context.WriteError($"Theme '{name}' not found. Available: {string.Join(", ", themes.Available)}");
                    return 1;
                }
            }

            context.Pickle.Config.Update(c =>
            {
                c.LightTheme = args[1];
                c.DarkTheme = args[2];
            });
        }

        themes.Apply(ThemeProvider.Auto);
        var config = context.Pickle.Config.Current;
        context.WriteHost(
            $"Theme follows the system: {config.LightTheme} in light mode, {config.DarkTheme} in dark mode. Now showing "
            + Ansi.Colorize(themes.Current.Name, themes.Current.Ui.Accent, bold: true) + ".");
        return 0;
    }

    private static int Import(PickleCommandContext context, List<string> args)
    {
        string? Option(string name)
        {
            var i = args.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Count)
            {
                return null;
            }

            var value = args[i + 1];
            args.RemoveRange(i, 2);
            return value;
        }

        bool Flag(string name) => args.RemoveAll(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;

        var themes = context.Pickle.Themes;
        var source = context.Pickle.Services.Get<ITerminalSchemeSource>();
        var themeName = Option("--name");
        var layoutName = Option("--layout") ?? "powerline";
        var schemeName = Option("--scheme");
        var force = Flag("--force");
        if (Flag("--list"))
        {
            var known = source?.Schemes() ?? [];
            if (known.Count == 0)
            {
                context.WriteHost("No color schemes found in the Windows Terminal settings. Import one from a JSON file instead: pk theme import scheme.json");
                return 0;
            }

            foreach (var scheme in known)
            {
                context.WriteHost(Swatches(scheme.Palette.Background, scheme.Palette.Red, scheme.Palette.Green, scheme.Palette.Yellow, scheme.Palette.Blue, scheme.Palette.Purple, scheme.Palette.Cyan)
                    + "  " + scheme.Name);
            }

            return 0;
        }

        var what = string.Join(' ', args).Trim();
        if (what.Length == 0 || args.Any(a => a.StartsWith("--", StringComparison.Ordinal)))
        {
            context.WriteError("Usage: pk theme import <scheme name | file.json> [--name <theme>] [--layout <theme>] [--scheme <name>] [--force]");
            return 2;
        }

        // A file path is relative to the shell's location, not the process's working directory.
        string path;
        try
        {
            path = Path.GetFullPath(what, context.Cwd);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            path = what;
        }

        var isFile = File.Exists(path);

        IReadOnlyList<TerminalScheme> candidates;
        try
        {
            candidates = isFile ? TerminalSchemes.Parse(File.ReadAllText(path)) : source?.Schemes() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            context.WriteError($"Couldn't read '{what}': {ex.Message}");
            return 1;
        }

        var wanted = schemeName ?? (isFile ? null : what);
        var matches = wanted is null ? candidates : [.. candidates.Where(c => c.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))];
        if (matches.Count != 1)
        {
            var names = candidates.Count == 0 ? "none found" : string.Join(", ", candidates.Select(c => c.Name));
            context.WriteError(matches.Count == 0
                ? $"No color scheme '{wanted ?? what}'. Schemes: {names}. (pk theme import --list shows Windows Terminal's.)"
                : $"'{what}' has {matches.Count} schemes; pick one with --scheme <name>: {names}");
            return 1;
        }

        if (themes.Load(layoutName) is not { } layout)
        {
            context.WriteError($"Layout theme '{layoutName}' not found. Available: {string.Join(", ", themes.Available)}");
            return 1;
        }

        var name = themeName ?? ThemeImport.ThemeName(matches[0].Name);
        if (!ThemeProvider.IsValidName(name) || ThemeProvider.IsAuto(name))
        {
            context.WriteError($"'{name}' can't be a theme name: use letters, digits, '-', '_' and '.'.");
            return 2;
        }

        if (!force && themes.Available.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            context.WriteError($"A theme called '{name}' already exists. Pick another with --name, or replace it with --force.");
            return 1;
        }

        var theme = ThemeImport.Create(matches[0], layout, name);
        var file = Path.Combine(context.Pickle.Paths.ThemesDir, name + ".json");
        Directory.CreateDirectory(context.Pickle.Paths.ThemesDir);
        File.WriteAllText(file, JsonSerializer.Serialize(theme, PickleJson.Options));

        var ui = themes.Current.Ui;
        context.WriteHost($"Imported \"{matches[0].Name}\" as " + Ansi.Colorize(name, ui.Accent, bold: true) + $" ({layout.Name} layout) → {file}");
        context.WriteHost(Ansi.Colorize("Use it with: pk theme set " + name, ui.Muted));
        return 0;
    }

    private static string AutoDescription(PickleCommandContext context, ThemeProvider provider)
    {
        var config = context.Pickle.Config.Current;
        var mode = provider.SystemPrefersLight switch
        {
            true => "light now",
            false => "dark now",
            null => "can't tell, so dark",
        };
        return $"Follows the system: {config.LightTheme} in light mode, {config.DarkTheme} in dark mode ({mode})";
    }

    private int Set(PickleCommandContext context, IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            context.WriteError("Usage: pk theme set <name>. Themes: " + string.Join(", ", context.Pickle.Themes.Available));
            return 2;
        }

        try
        {
            context.Pickle.Themes.Apply(args[1]);
        }
        catch (ArgumentException ex)
        {
            context.WriteError(ex.Message);
            return 1;
        }

        var theme = context.Pickle.Themes.Current;
        context.WriteHost("Theme set to " + Ansi.Colorize(theme.Name, theme.Ui.Accent, bold: true) + ".");
        return 0;
    }

    private int Show(PickleCommandContext context, string? name)
    {
        var themes = context.Pickle.Themes;
        var theme = name is null ? themes.Current : themes.Load(name);
        if (theme is null)
        {
            context.WriteError($"Theme '{name}' not found. Available: {string.Join(", ", themes.Available)}");
            return 1;
        }

        var ui = themes.Current.Ui;
        string Label(string text) => Ansi.Colorize(text.PadRight(9), ui.Muted);

        context.WriteHost(Ansi.Colorize(theme.Name, theme.Ui.Accent, bold: true) + (theme.Description is { } d ? "  " + Ansi.Colorize(d, ui.Muted) : string.Empty));
        var t = theme.Terminal;
        context.WriteHost(Label("palette") + Swatches(t.Black, t.Red, t.Green, t.Yellow, t.Blue, t.Purple, t.Cyan, t.White));
        context.WriteHost(Label(string.Empty) + Swatches(t.BrightBlack, t.BrightRed, t.BrightGreen, t.BrightYellow, t.BrightBlue, t.BrightPurple, t.BrightCyan, t.BrightWhite));
        context.WriteHost(Label("syntax") + SyntaxSample(theme));
        var u = theme.Ui;
        context.WriteHost(Label("ui") + string.Join(' ', new[]
        {
            Ansi.Colorize("accent", u.Accent), Ansi.Colorize("muted", u.Muted), Ansi.Colorize("success", u.Success),
            Ansi.Colorize("warning", u.Warning), Ansi.Colorize("error", u.Error), Ansi.Colorize("info", u.Info),
        }));

        var first = true;
        foreach (var line in PreviewLines(theme, PreviewWidth() - 9))
        {
            context.WriteHost((first ? Label("prompt") : Label(string.Empty)) + line);
            first = false;
        }

        return 0;
    }

    private int Preview(PickleCommandContext context, IReadOnlyList<string> names)
    {
        var themes = context.Pickle.Themes;
        var ui = themes.Current.Ui;
        var width = PreviewWidth();
        foreach (var name in names.Count > 0 ? names : themes.Available)
        {
            if (themes.Load(name) is not { } theme)
            {
                context.WriteError($"Theme '{name}' not found.");
                continue;
            }

            var current = theme.Name.Equals(themes.Current.Name, StringComparison.OrdinalIgnoreCase);
            context.WriteHost(Ansi.Colorize(theme.Name, ui.Accent, bold: true) + (current ? Ansi.Colorize(" (current)", ui.Muted) : string.Empty));
            foreach (var line in PreviewLines(theme, width - 2))
            {
                context.WriteHost("  " + line + Ansi.Reset);
            }

            context.WriteHost(string.Empty);
        }

        return 0;
    }

    private IReadOnlyList<string> PreviewLines(Theme theme, int width) =>
        ThemePreview.ToLines(engine.RenderPreview(theme, width), width);

    private int PreviewWidth()
    {
        var width = engine.TerminalWidth;
        return width <= 20 ? 80 : Math.Min(width, 100);
    }

    private int Help(PickleCommandContext context)
    {
        context.WriteHost(Usage);
        return 0;
    }

    private int Unknown(PickleCommandContext context, string sub)
    {
        context.WriteError($"Unknown subcommand '{sub}'. Usage: {Usage}");
        return 2;
    }

    private static string Swatches(params string[] colors)
    {
        var sb = new StringBuilder();
        foreach (var color in colors)
        {
            sb.Append(Ansi.Colorize("   ", null, color));
        }

        return sb.ToString();
    }

    private static string SyntaxSample(Theme theme)
    {
        var s = theme.Syntax;
        return Ansi.Colorize("Get-ChildItem", s.Command)
            + Ansi.Colorize(" -Path ", s.Parameter)
            + Ansi.Colorize("'src'", s.String)
            + Ansi.Colorize(" | ", s.Operator)
            + Ansi.Colorize("Where-Object", s.Command)
            + " { "
            + Ansi.Colorize("$_", s.Variable)
            + Ansi.Colorize(".Length", s.Member)
            + Ansi.Colorize(" -gt ", s.Operator)
            + Ansi.Colorize("1kb", s.Number)
            + " } "
            + Ansi.Colorize("# big files", s.Comment);
    }
}
