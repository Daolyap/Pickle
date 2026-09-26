using Pickle.Abstractions;

namespace Pickle.Core.Hosting;

/// <summary>
/// The first interactive start: a short welcome, then the yes/no offers plugins registered (<see cref="IFirstRunOffers"/>).
/// <c>shell.setupVersion</c> remembers the newest setup asked, so offers added by a later version (<see
/// cref="FirstRunOffer.Since"/>) are asked once after an upgrade; <c>pk setup</c> asks every relevant offer again.
/// </summary>
public sealed class FirstRun : IFirstRunOffers
{
    /// <summary>Bump when adding offers that people who already went through setup should see.</summary>
    public const int SetupVersion = 2;

    private readonly List<FirstRunOffer> _offers = [];

    public IReadOnlyList<FirstRunOffer> All
    {
        get
        {
            lock (_offers)
            {
                return [.. _offers];
            }
        }
    }

    public void Add(FirstRunOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        lock (_offers)
        {
            _offers.RemoveAll(o => o.Id == offer.Id);
            _offers.Add(offer);
        }
    }

    /// <summary>Asks what this user has not been asked yet (nothing when setup is current) and records the version.</summary>
    internal void Run(PickleRuntime runtime)
    {
        var shell = runtime.Config.Current.Shell;
        var asked = Math.Max(shell.SetupVersion, shell.FirstRunCompleted ? 1 : 0);
        if (asked >= SetupVersion)
        {
            return;
        }

        if (!shell.FirstRunCompleted)
        {
            Welcome(runtime);
        }

        Ask(runtime, [.. All.Where(o => o.Since > asked)], header: shell.FirstRunCompleted ? "New in this version of Pickle:" : null);

        try
        {
            runtime.Config.Update(c => (c.Shell.FirstRunCompleted, c.Shell.SetupVersion) = (true, SetupVersion));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            runtime.Log.Warn("first-run", "Could not save shell.setupVersion", ex);
        }
    }

    /// <summary><c>pk setup</c>: every offer that applies right now. Returns how many were asked.</summary>
    internal int RunAgain(PickleRuntime runtime) => Ask(runtime, All, header: null);

    private int Ask(PickleRuntime runtime, IReadOnlyList<FirstRunOffer> offers, string? header)
    {
        var terminal = runtime.Terminal;
        var theme = runtime.Themes.Current;
        var count = 0;
        foreach (var offer in offers)
        {
            bool relevant;
            try
            {
                relevant = offer.IsRelevant();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                runtime.Log.Warn("first-run", $"'{offer.Id}' could not check whether it applies", ex);
                continue;
            }

            if (!relevant)
            {
                continue;
            }

            if (count++ == 0 && header is not null)
            {
                terminal.Write(Ansi.Colorize(header, theme.Ui.Accent, bold: true) + "\r\n");
            }

            terminal.Write(Ansi.Colorize("? ", theme.Ui.Accent, bold: true) + offer.Question + Ansi.Colorize(" [Y/n] ", theme.Ui.Muted));
            terminal.Flush();
            var yes = ReadYesNo(terminal);
            terminal.Write((yes ? "yes" : "no") + "\r\n");
            if (!yes)
            {
                continue;
            }

            try
            {
                if (offer.Progress is { } progress)
                {
                    terminal.Write(Ansi.Colorize("  " + progress, theme.Ui.Muted) + "\r\n");
                    terminal.Flush();
                }

                terminal.Write(Ansi.Colorize("  " + offer.Accept(), theme.Ui.Success) + "\r\n");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                runtime.Log.Warn("first-run", $"'{offer.Id}' failed", ex);
                terminal.Write(Ansi.Colorize("  " + ex.Message, theme.Ui.Error) + "\r\n");
            }
        }

        if (count > 0)
        {
            terminal.Write(Ansi.Colorize("  Change your mind later with 'pk setup'.", theme.Ui.Muted) + "\r\n\r\n");
        }

        return count;
    }

    private static void Welcome(PickleRuntime runtime)
    {
        var terminal = runtime.Terminal;
        var ui = runtime.Themes.Current.Ui;
        string Key(string key, string what) => Ansi.Colorize(key, ui.Accent, bold: true) + " " + Ansi.Colorize(what, ui.Muted);

        terminal.Write("\r\n" + Ansi.Colorize("Welcome to Pickle!", ui.Accent, bold: true) + " PowerShell 7, with a friendlier front end.\r\n");
        terminal.Write("  " + string.Join("   ", Key("F1", "all commands"), Key("Tab", "completion"), Key("→", "accept suggestion"), Key("Ctrl+R", "history")) + "\r\n");
        terminal.Write("  " + string.Join("   ", Key("Alt+G", "git"), Key("Alt+P", "processes"), Key("pk help", "everything else"), Key("pk theme", "looks")) + "\r\n\r\n");
    }

    private static bool ReadYesNo(Terminal.ITerminal terminal)
    {
        terminal.SetEditMode(true);
        try
        {
            while (true)
            {
                var key = terminal.ReadKey();
                switch (key.Key)
                {
                    case ConsoleKey.Enter or ConsoleKey.Y:
                        return true;
                    case ConsoleKey.N or ConsoleKey.Escape:
                        return false;
                }

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    return false;
                }
            }
        }
        finally
        {
            terminal.SetEditMode(false);
        }
    }
}
