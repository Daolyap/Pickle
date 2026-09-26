using System.Globalization;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Terminal;

/// <summary>
/// Registers <c>pk terminal</c> and keeps an installed fragment in sync with the theme and <c>terminal.*</c> config.
/// Call from the Windows plugin's Initialize.
/// </summary>
public static class WindowsTerminalIntegration
{
    public static void Register(IPickleContext context) => Register(context, WindowsTerminalLocations.ForCurrentUser());

    /// <summary>Tests pass explicit locations (which also enables the command on non-Windows systems).</summary>
    public static void Register(IPickleContext context, WindowsTerminalLocations? locations, Func<string?>? executablePath = null)
    {
        var manager = locations is null ? null : new WindowsTerminalManager(locations);
        executablePath ??= () => Environment.ProcessPath;
        TerminalSettings Appearance() => TerminalFonts.ForFragment(context.Config.Current.Terminal, context.Services.Get<IFontService>());
        context.Commands.Register(new TerminalCommand(manager, executablePath, Appearance));
        if (manager is null)
        {
            return;
        }

        bool MachineProfile() => manager.Locations.MachineFragmentFile is { } machine && File.Exists(machine);

        if (context.Services.Get<IFirstRunOffers>() is { } offers)
        {
            if (context.Services.Get<IFontService>() is { } fonts)
            {
                offers.Add(new FirstRunOffer(
                    "nerd-font",
                    $"Install the {fonts.RecommendedFont} font so the prompt's icons display (about 5 MB, just for you)?",
                    () => manager.Locations.TerminalPresent && !fonts.IsInstalled(fonts.RecommendedFont) && fonts.InstalledNerdFonts().Count == 0,
                    () =>
                    {
                        var result = fonts.InstallRecommendedAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                        if (!result.Success)
                        {
                            throw new IOException(result.Message);
                        }

                        return result.Message + " Restart Windows Terminal to use it.";
                    })
                { Since = 2, Progress = $"Downloading {fonts.RecommendedFont}…" });
            }

            offers.Add(new FirstRunOffer(
                "windows-terminal",
                MachineProfile()
                    ? "Keep Windows Terminal's Pickle profile in step with your theme and font?"
                    : "Add a Pickle profile to Windows Terminal?",
                () => manager.Locations.TerminalPresent && !manager.IsInstalled && !string.IsNullOrEmpty(executablePath()),
                () =>
                {
                    manager.Install(executablePath()!, Appearance(), TerminalFonts.Palette(context));
                    return $"Added the '{WindowsTerminalFragment.ProfileName}' profile. It shows up in new Windows Terminal windows (⌄ menu).";
                })
            { Since = 2 });

            offers.Add(new FirstRunOffer(
                "windows-terminal-default",
                "Make Pickle the default profile in Windows Terminal (new windows and tabs open Pickle)?",
                () => manager.Locations.TerminalPresent && (manager.IsInstalled || MachineProfile())
                    && manager.SettingsStatus() is { Count: > 0 } status && !status.Any(s => s.IsDefault),
                () =>
                {
                    var results = manager.SetDefaultProfile();
                    if (results.FirstOrDefault(r => r.Error is not null) is { } failed)
                    {
                        throw new IOException($"{failed.SettingsFile}: {failed.Error}");
                    }

                    return "Pickle is now Windows Terminal's default profile" + (results.FirstOrDefault(r => r.BackupFile is not null) is { } r
                        ? $" (backup: {r.BackupFile})."
                        : ".");
                })
            { Since = 2 });
        }

        void Regenerate()
        {
            try
            {
                if (manager.Update(Appearance(), TerminalFonts.Palette(context)))
                {
                    context.Log.Info("terminal", $"Updated {manager.Locations.FragmentFile}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.Log.Warn("terminal", "Could not update the Windows Terminal fragment", ex);
            }
        }

        // Also repairs a profile written before Pickle checked fonts (it named Cascadia Code NF whether or not it was
        // installed, and Terminal warned about the missing font at every start).
        Regenerate();
        if (context.Services.Get<IFontService>() is { } installer)
        {
            installer.FontsChanged += (_, _) => Regenerate();
        }

        context.Themes.ThemeChanged += (_, _) => Regenerate();
        context.Config.Changed += (_, e) =>
        {
            if (e.Path is null || e.Path.StartsWith("terminal", StringComparison.OrdinalIgnoreCase))
            {
                Regenerate();
            }
        };
    }
}

/// <summary>What goes into the profile: only installed fonts, and the configured theme's colours.</summary>
public static class TerminalFonts
{
    /// <summary>
    /// The configured theme's palette. An elevated session shows the admin theme, which must not leak into the
    /// profile that normal sessions use.
    /// </summary>
    public static TerminalPalette Palette(IPickleContext context) =>
        context.Themes.Load(context.Config.Current.Theme)?.Terminal ?? context.Themes.Current.Terminal;

    /// <summary>
    /// The appearance to write into a profile: <see cref="TerminalSettings.FontFace"/> (a comma-separated fallback list
    /// is allowed) keeps only installed faces, and is dropped (Terminal's default font) when none is installed.
    /// </summary>
    public static TerminalSettings ForFragment(TerminalSettings settings, IFontService? fonts) =>
        ForFragment(settings, fonts is null ? null : fonts.IsInstalled);

    public static TerminalSettings ForFragment(TerminalSettings settings, Func<string, bool>? isInstalled)
    {
        if (isInstalled is null || string.IsNullOrWhiteSpace(settings.FontFace))
        {
            return settings;
        }

        var faces = settings.FontFace.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(isInstalled).ToList();
        var face = faces.Count == 0 ? null : string.Join(", ", faces);
        if (face == settings.FontFace.Trim())
        {
            return settings;
        }

        return new TerminalSettings
        {
            FontFace = face,
            FontSize = settings.FontSize,
            Opacity = settings.Opacity,
            UseAcrylic = settings.UseAcrylic,
            CursorShape = settings.CursorShape,
            BackgroundImage = settings.BackgroundImage,
            BackgroundImageOpacity = settings.BackgroundImageOpacity,
            Padding = settings.Padding,
        };
    }
}

/// <summary><c>pk terminal install|uninstall|status|set &lt;key&gt; &lt;value&gt;|default</c>.</summary>
public sealed class TerminalCommand(WindowsTerminalManager? manager, Func<string?> executablePath, Func<TerminalSettings>? appearance = null) : IPickleCommand
{
    private static readonly string[] CursorShapes = ["bar", "vintage", "underscore", "filledBox", "emptyBox", "doubleUnderscore"];

    public string Name => "terminal";

    public string Description => "Windows Terminal profile: install, appearance, make Pickle the default";

    public string Usage => "pk terminal install | uninstall | status | set <font|fontsize|opacity|acrylic|cursor|background|padding> <value> | default";

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (manager is null)
        {
            context.WriteError(WindowsTerminalProfile.WindowsOnlyMessage);
            return ValueTask.FromResult(1);
        }

        var sub = args.Count == 0 ? "status" : args[0].ToLowerInvariant();
        int code;
        try
        {
            code = sub switch
            {
                "install" => Install(context, manager),
                "uninstall" or "remove" => Uninstall(context, manager),
                "status" => Status(context, manager),
                "set" => Set(context, manager, args),
                "default" => Default(context, manager),
                "help" or "-h" or "--help" => Help(context),
                _ => Fail(context, $"Unknown subcommand '{args[0]}'. Usage: {Usage}", 2),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            code = Fail(context, ex.Message, 1);
        }

        return ValueTask.FromResult(code);
    }

    private int Install(PickleCommandContext context, WindowsTerminalManager m)
    {
        var exe = executablePath();
        if (string.IsNullOrEmpty(exe))
        {
            return Fail(context, "Cannot determine the path of the pickle executable.", 1);
        }

        var pickle = context.Pickle;
        m.Install(exe, Appearance(pickle), TerminalFonts.Palette(pickle));
        context.WriteHost($"Installed the Windows Terminal profile '{WindowsTerminalFragment.ProfileName}': {m.Locations.FragmentFile}");
        context.WriteHost("Restart Windows Terminal to pick it up. 'pk terminal default' makes it the default profile.");
        return 0;
    }

    private static int Uninstall(PickleCommandContext context, WindowsTerminalManager m)
    {
        context.WriteHost(m.Uninstall()
            ? $"Removed the Windows Terminal profile ({m.Locations.FragmentFile})."
            : "The Windows Terminal profile is not installed.");
        if (m.Locations.MachineFragmentFile is { } machine && File.Exists(machine))
        {
            context.WriteHost("The installer's all-users Pickle profile stays; uninstall Pickle to remove it.");
            return 0;
        }

        foreach (var result in m.RestoreDefaultProfile())
        {
            context.WriteHost(result.Error is null
                ? $"Windows Terminal's default profile no longer points at Pickle ({result.SettingsFile})."
                : $"Could not change the default profile in {result.SettingsFile}: {result.Error}");
        }

        return 0;
    }

    private static int Status(PickleCommandContext context, WindowsTerminalManager m)
    {
        var pickle = context.Pickle;
        var ui = pickle.Themes.Current.Ui;
        string Label(string text) => Ansi.Colorize(text.PadRight(11), ui.Muted);

        context.WriteHost("Windows Terminal profile: " + (m.IsInstalled ? Ansi.Colorize("installed", ui.Success) : Ansi.Colorize("not installed", ui.Warning)));
        context.WriteHost(Label("fragment") + m.Locations.FragmentFile);
        if (m.Locations.MachineFragmentFile is { } machine && File.Exists(machine))
        {
            context.WriteHost(Label("all users") + machine + Ansi.Colorize(" (from the installer; a per-user profile overrides it)", ui.Muted));
        }

        if (m.InstalledExecutable is { } exe)
        {
            context.WriteHost(Label("launches") + exe);
        }

        context.WriteHost(Label("profile") + WindowsTerminalFragment.ProfileGuidString);
        context.WriteHost(Label("scheme") + $"{WindowsTerminalFragment.SchemeName} (from theme '{pickle.Themes.Current.Name}')");
        context.WriteHost(Label("appearance") + Describe(pickle.Config.Current.Terminal));
        var settings = m.SettingsStatus();
        if (settings.Count == 0)
        {
            context.WriteHost(Label("settings") + "no Windows Terminal settings.json found");
        }

        foreach (var (path, isDefault) in settings)
        {
            context.WriteHost(Label("settings") + path + (isDefault ? Ansi.Colorize("  (Pickle is the default profile)", ui.Success) : string.Empty));
        }

        if (!m.IsInstalled)
        {
            context.WriteHost("Run 'pk terminal install' to add Pickle to Windows Terminal.");
        }

        return 0;
    }

    private int Set(PickleCommandContext context, WindowsTerminalManager m, IReadOnlyList<string> args)
    {
        if (args.Count < 3)
        {
            return Fail(context, "Usage: pk terminal set <font|fontsize|opacity|acrylic|cursor|background|padding> <value>", 2);
        }

        var key = args[1].ToLowerInvariant();
        var values = args.Skip(2).ToList();
        var value = string.Join(' ', values).Trim();
        var clear = value.ToLowerInvariant() is "none" or "default" or "off" or "clear";
        Action<TerminalSettings> apply;
        string shown;
        switch (key)
        {
            case "font" or "fontface" or "face":
                apply = t => t.FontFace = clear ? null : value;
                shown = clear ? "(terminal default)" : value;
                break;
            case "fontsize" or "size":
                if (!double.TryParse(value.TrimEnd('p', 't'), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || size < 4 || size > 128)
                {
                    return Fail(context, "Font size must be a number between 4 and 128.", 2);
                }

                apply = t => t.FontSize = size;
                shown = size.ToString(CultureInfo.InvariantCulture);
                break;
            case "opacity":
                if (!int.TryParse(value.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var opacity) || opacity < 0 || opacity > 100)
                {
                    return Fail(context, "Opacity must be a whole number from 0 to 100.", 2);
                }

                apply = t => t.Opacity = opacity;
                shown = opacity.ToString(CultureInfo.InvariantCulture) + "%";
                break;
            case "acrylic":
                if (ParseBool(value) is not { } acrylic)
                {
                    return Fail(context, "Acrylic must be on or off.", 2);
                }

                apply = t => t.UseAcrylic = acrylic;
                shown = acrylic ? "on" : "off";
                break;
            case "cursor" or "cursorshape":
                if (CursorShapes.FirstOrDefault(s => s.Equals(value, StringComparison.OrdinalIgnoreCase)) is not { } shape)
                {
                    return Fail(context, "Cursor shape must be one of: " + string.Join(", ", CursorShapes), 2);
                }

                apply = t => t.CursorShape = shape;
                shown = shape;
                break;
            case "background" or "backgroundimage" or "image":
                return SetBackground(context, m, values, clear);
            case "padding":
                if (!IsValidPadding(value))
                {
                    return Fail(context, "Padding is 1, 2 or 4 comma-separated numbers, e.g. 8 or \"8, 4\".", 2);
                }

                apply = t => t.Padding = value;
                shown = value;
                break;
            default:
                return Fail(context, $"Unknown setting '{args[1]}'. Settings: font, fontsize, opacity, acrylic, cursor, background, padding.", 2);
        }

        return Save(context, m, apply, $"{key} = {shown}");
    }

    private int SetBackground(PickleCommandContext context, WindowsTerminalManager m, List<string> values, bool clear)
    {
        if (clear)
        {
            return Save(context, m, t => (t.BackgroundImage, t.BackgroundImageOpacity) = (null, null), "background = none");
        }

        double? opacity = null;
        if (values.Count > 1 && double.TryParse(values[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var o) && o is >= 0 and <= 1)
        {
            opacity = o;
            values = values[..^1];
        }

        var path = string.Join(' ', values).Trim().Trim('"');
        if (path.StartsWith('~'))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        }

        if (!Path.IsPathFullyQualified(path))
        {
            path = Path.GetFullPath(Path.Combine(context.Cwd, path));
        }

        if (!File.Exists(path))
        {
            context.WriteHost($"Note: '{path}' does not exist (yet).");
        }

        return Save(
            context,
            m,
            t => (t.BackgroundImage, t.BackgroundImageOpacity) = (path, opacity ?? t.BackgroundImageOpacity),
            "background = " + path + (opacity is { } op ? $" (opacity {op.ToString(CultureInfo.InvariantCulture)})" : string.Empty));
    }

    private int Save(PickleCommandContext context, WindowsTerminalManager m, Action<TerminalSettings> apply, string description)
    {
        var pickle = context.Pickle;
        pickle.Config.Update(c => apply(c.Terminal));
        context.WriteHost("terminal." + description);
        var written = Appearance(pickle);
        if (!string.IsNullOrWhiteSpace(pickle.Config.Current.Terminal.FontFace) && written.FontFace != pickle.Config.Current.Terminal.FontFace)
        {
            context.WriteHost($"Note: '{pickle.Config.Current.Terminal.FontFace}' is not installed, so the profile uses Terminal's default font until it is. 'pk font install' installs {pickle.Services.Get<IFontService>()?.RecommendedFont ?? "a Nerd Font"}.");
        }

        if (m.IsInstalled)
        {
            m.Update(written, TerminalFonts.Palette(pickle));
            context.WriteHost("Updated the Windows Terminal profile.");
        }
        else
        {
            context.WriteHost("Run 'pk terminal install' to apply it to Windows Terminal.");
        }

        return 0;
    }

    private int Default(PickleCommandContext context, WindowsTerminalManager m)
    {
        if (!m.IsInstalled && Install(context, m) != 0)
        {
            return 1;
        }

        var results = m.SetDefaultProfile();
        if (results.Count == 0)
        {
            return Fail(context, "Windows Terminal settings.json was not found. Start Windows Terminal once, then try again.", 1);
        }

        var failed = false;
        foreach (var r in results)
        {
            if (r.Error is not null)
            {
                failed = true;
                context.WriteError($"{r.SettingsFile}: {r.Error}");
            }
            else if (r.Changed)
            {
                context.WriteHost($"Pickle is now the default profile in {r.SettingsFile} (backup: {r.BackupFile}).");
            }
            else
            {
                context.WriteHost($"Pickle is already the default profile in {r.SettingsFile}.");
            }
        }

        return failed ? 1 : 0;
    }

    private TerminalSettings Appearance(IPickleContext pickle) => appearance?.Invoke() ?? pickle.Config.Current.Terminal;

    private int Help(PickleCommandContext context)
    {
        context.WriteHost(Usage);
        return 0;
    }

    private static int Fail(PickleCommandContext context, string message, int code)
    {
        context.WriteError(message);
        return code;
    }

    private static string Describe(TerminalSettings t)
    {
        var c = CultureInfo.InvariantCulture;
        return string.Join(" · ", new[]
        {
            (t.FontFace ?? "default font") + (t.FontSize is { } s ? " " + s.ToString(c) + "pt" : string.Empty),
            "opacity " + (t.Opacity is { } o ? o.ToString(c) + "%" : "default"),
            "acrylic " + (t.UseAcrylic ? "on" : "off"),
            "cursor " + t.CursorShape,
            "padding " + (t.Padding ?? "default"),
            "background " + (t.BackgroundImage ?? "none"),
        });
    }

    private static bool? ParseBool(string value) => value.ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "1" => true,
        "off" or "false" or "no" or "0" => false,
        _ => null,
    };

    public static bool IsValidPadding(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length is 1 or 2 or 4
            && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 0 && n <= 100);
    }
}
