namespace Pickle.Core.Translation;

/// <summary>The POSIX-style functions exported by the embedded Pickle.Translate module (kept in sync by a test).</summary>
public static class ShimCatalog
{
    public static IReadOnlyList<(string Name, string Description)> Shims { get; } =
    [
        ("alias", "alias name='body' → a persistent Pickle alias"),
        ("cat", "Print files (-n numbers lines)"),
        ("chmod", "Windows has no exec bit; 600/700 restrict the ACL to you (icacls)"),
        ("clear", "Clear the screen"),
        ("cp", "Copy (-r -f -v -n)"),
        ("df", "Disk free per drive (-h)"),
        ("du", "Disk usage (-s -h -d N)"),
        ("env", "List environment variables, or run a command with extra ones"),
        ("export", "export NAME=value → $env:NAME (for scripts; typed lines are translated)"),
        ("find", "find [path] -name/-iname/-type f|d/-maxdepth"),
        ("grep", "Search text (-r -n -i -v -l -c -E -F -w --include), files or pipeline"),
        ("head", "First lines/bytes (-n -c)"),
        ("history", "Command history (history N, history -c)"),
        ("kill", "Stop processes (-9, -SIGTERM, %job)"),
        ("ln", "Links (-s symbolic, -f)"),
        ("ls", "List files (-l -a -h -R -t -S -r -1 -d)"),
        ("man", "Help for a command"),
        ("mkdir", "Create directories (-p -v)"),
        ("mv", "Move/rename (-f -v -n)"),
        ("open", "Open a file/URL with its default app"),
        ("ps", "Processes (ps, ps aux, ps -ef)"),
        ("rm", "Remove (-r -f -i -v); refuses roots and your home directory"),
        ("source", "Run a script (typed lines become '. file')"),
        ("tail", "Last lines/bytes (-n -c, -f follow)"),
        ("touch", "Create files / update timestamps (-d date, -c)"),
        ("type", "How a name resolves (alias, function, cmdlet, file)"),
        ("unalias", "Remove a Pickle alias"),
        ("uname", "System information (-a -s -n -r -m -o)"),
        ("unset", "Remove environment variables"),
        ("wc", "Count lines/words/bytes (-l -w -c)"),
        ("which", "Locate a command (-a)"),
        ("xdg-open", "Open a file/URL with its default app"),
    ];
}
