using Pickle.Abstractions;

namespace Pickle.Core.Hosting;

/// <summary>
/// Offers plugins registered for the first interactive start (<see cref="IFirstRunOffers"/>). Each relevant offer is a
/// single-key yes/no question; afterwards <c>shell.firstRunCompleted</c> is saved so they are never asked again.
/// </summary>
public sealed class FirstRun : IFirstRunOffers
{
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

    /// <summary>Asks the relevant offers (if this is the first run) and marks the first run as done.</summary>
    internal void Run(PickleRuntime runtime)
    {
        if (runtime.Config.Current.Shell.FirstRunCompleted)
        {
            return;
        }

        var terminal = runtime.Terminal;
        var theme = runtime.Themes.Current;
        foreach (var offer in All)
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
                terminal.Write(Ansi.Colorize("  " + offer.Accept(), theme.Ui.Success) + "\r\n");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                runtime.Log.Warn("first-run", $"'{offer.Id}' failed", ex);
                terminal.Write(Ansi.Colorize("  " + ex.Message, theme.Ui.Error) + "\r\n");
            }
        }

        try
        {
            runtime.Config.Update(c => c.Shell.FirstRunCompleted = true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            runtime.Log.Warn("first-run", "Could not save shell.firstRunCompleted", ex);
        }
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
