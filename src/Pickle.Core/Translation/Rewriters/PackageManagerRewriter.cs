using Pickle.Abstractions;

namespace Pickle.Core.Translation.Rewriters;

/// <summary>
/// Windows only: <c>apt/apt-get/brew/dnf/yum/zypper/pacman/snap/choco/apk</c> → winget. <c>install</c> → <c>pk winget install</c>
/// (plain <c>winget install</c> when the winget plugin isn't loaded), <c>search</c> → <c>winget search</c>,
/// <c>apt update &amp;&amp; apt upgrade</c> → <c>pk upgrade</c>. A manager that really exists on PATH (e.g. choco) is left alone.
/// </summary>
public sealed class PackageManagerRewriter : IInputRewriter
{
    private static readonly HashSet<string> Managers = new(StringComparer.OrdinalIgnoreCase)
    {
        "apt", "apt-get", "aptitude", "brew", "dnf", "yum", "zypper", "pacman", "snap", "choco", "apk",
    };

    private readonly bool _isWindows;
    private readonly Func<string, bool> _commandExists;
    private readonly Func<string, bool> _hasPickleCommand;

    public PackageManagerRewriter(bool isWindows, Func<string, bool> commandExists, Func<string, bool> hasPickleCommand)
    {
        _isWindows = isWindows;
        _commandExists = commandExists;
        _hasPickleCommand = hasPickleCommand;
    }

    private enum Verb
    {
        Install,
        Search,
        Remove,
        Upgrade,
        Update,
    }

    private sealed record Action(string Manager, Verb Verb, IReadOnlyList<string> Packages);

    public string Name => "packages";

    public int Order => 30;

    public RewriteResult? Rewrite(string input, RewriteContext context)
    {
        if (!_isWindows)
        {
            return null;
        }

        var segments = ShellLexer.Segments(input);
        var actions = segments.Select(Classify).ToList();
        if (actions.All(a => a is null))
        {
            return null;
        }

        var edits = new TextEdits();
        string? manager = null;
        for (var i = 0; i < segments.Count; i++)
        {
            if (actions[i] is not { } action)
            {
                continue;
            }

            manager ??= action.Manager;
            var next = i + 1 < segments.Count ? actions[i + 1] : null;
            if (action.Verb == Verb.Update && next is { Verb: Verb.Upgrade, Packages.Count: 0 }
                && segments[i].After is ShellSeparator.AndAnd or ShellSeparator.Semicolon or ShellSeparator.Newline)
            {
                // `apt update && apt upgrade`: winget refreshes sources as part of upgrading.
                edits.Replace(segments[i].Start, segments[i + 1].Start, string.Empty);
                continue;
            }

            edits.Replace(segments[i].Start, segments[i].End, Emit(action, segments[i].IsChained ? " && " : "; "));
        }

        return new RewriteResult(edits.Apply(input), $"{manager} isn't available on Windows; winget is the Windows package manager");
    }

    private Action? Classify(ShellSegment segment)
    {
        var words = segment.Words;
        var index = 0;
        if (words.Count > 0 && words[0].Literal == "sudo")
        {
            index++;
            while (index < words.Count && words[index].Literal is "-E" or "-H")
            {
                index++;
            }
        }

        if (index >= words.Count || words[index].Literal is not { } manager || !Managers.Contains(manager))
        {
            return null;
        }

        var args = new List<string>();
        foreach (var word in words.Skip(index + 1))
        {
            if (word.Literal is not { } literal)
            {
                return null;
            }

            args.Add(literal);
        }

        if (_commandExists(manager))
        {
            return null;
        }

        manager = manager.ToLowerInvariant();
        var action = manager == "pacman" ? ClassifyPacman(args) : ClassifyVerb(manager, args);
        return action is null ? null : action with { Manager = manager };
    }

    private static Action? ClassifyPacman(List<string> args)
    {
        var flag = args.FirstOrDefault(a => a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal));
        if (flag is null || flag.Length < 2)
        {
            return null;
        }

        var packages = Packages(args);
        var letters = flag[2..];
        return flag[1] switch
        {
            'S' when letters.Contains('s', StringComparison.Ordinal) => new Action("pacman", Verb.Search, packages),
            'S' when letters.Contains('u', StringComparison.Ordinal) => new Action("pacman", Verb.Upgrade, packages),
            'S' when packages.Count == 0 && letters.Contains('y', StringComparison.Ordinal) => new Action("pacman", Verb.Update, packages),
            'S' when packages.Count > 0 => new Action("pacman", Verb.Install, packages),
            'R' when packages.Count > 0 => new Action("pacman", Verb.Remove, packages),
            _ => null,
        };
    }

    private static Action? ClassifyVerb(string manager, List<string> args)
    {
        var verbIndex = args.FindIndex(a => !a.StartsWith('-'));
        if (verbIndex < 0)
        {
            return null;
        }

        var packages = Packages(args.Skip(verbIndex + 1));
        Verb? verb = args[verbIndex].ToLowerInvariant() switch
        {
            "install" or "in" or "add" => Verb.Install,
            "search" or "se" or "find" => Verb.Search,
            "remove" or "uninstall" or "purge" or "erase" or "rm" or "del" => Verb.Remove,
            "upgrade" or "full-upgrade" or "dist-upgrade" or "up" or "dup" or "refresh" => Verb.Upgrade,
            "update" when manager is "dnf" or "yum" or "zypper" => Verb.Upgrade,
            "update" or "ref" => Verb.Update,
            _ => null,
        };

        if (verb is null || (verb is Verb.Install or Verb.Search or Verb.Remove && packages.Count == 0))
        {
            return null;
        }

        if (verb == Verb.Upgrade && packages is ["all"])
        {
            packages = [];
        }

        return new Action(manager, verb.Value, packages);
    }

    private static List<string> Packages(IEnumerable<string> args) => [.. args.Where(a => !a.StartsWith('-'))];

    private string Emit(Action action, string joiner)
    {
        var winget = _hasPickleCommand("winget") ? "pk winget" : "winget";
        return action.Verb switch
        {
            Verb.Install => string.Join(joiner, action.Packages.Select(p => $"{winget} install {PowerShellText.QuoteIfNeeded(p)}")),
            Verb.Search => "winget search " + PowerShellText.QuoteIfNeeded(string.Join(' ', action.Packages)),
            Verb.Remove => string.Join(joiner, action.Packages.Select(p => "winget uninstall " + PowerShellText.QuoteIfNeeded(p))),
            Verb.Upgrade when action.Packages.Count > 0 => string.Join(joiner, action.Packages.Select(p => "winget upgrade " + PowerShellText.QuoteIfNeeded(p))),
            Verb.Upgrade => _hasPickleCommand("upgrade") ? "pk upgrade" : "winget upgrade --all",
            _ => "winget source update",
        };
    }
}
