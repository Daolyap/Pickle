namespace Pickle.Core;

/// <summary>Parsed command-line options for pickle.exe.</summary>
public sealed class PickleOptions
{
    /// <summary>-c / -Command: run and exit.</summary>
    public string? Command { get; set; }

    /// <summary>-f / -File or first positional *.ps1: run script and exit.</summary>
    public string? File { get; set; }

    public List<string> FileArguments { get; set; } = [];

    /// <summary>Read commands from stdin line by line without the interactive UI (tests, pipes).</summary>
    public bool Headless { get; set; }

    public bool NoProfile { get; set; }

    public bool NoPlugins { get; set; }

    public bool NoLogo { get; set; }

    public bool ShowVersion { get; set; }

    public bool ShowHelp { get; set; }

    public string? LogLevel { get; set; }

    /// <summary>Internal: run as the elevated helper (pipe name, nonce).</summary>
    public string? ElevatedHelperPipe { get; set; }

    public string? ElevatedHelperNonce { get; set; }

    /// <summary>Install the Windows Terminal profile fragment and exit.</summary>
    public bool InstallTerminalProfile { get; set; }

    public bool UninstallTerminalProfile { get; set; }

    /// <summary>Write a Windows Terminal fragment JSON to this path and exit (used by the MSI build).</summary>
    public string? WriteTerminalFragment { get; set; }

    /// <summary>Profile commandline for <see cref="WriteTerminalFragment"/> (default: this executable's path).</summary>
    public string? FragmentCommandLine { get; set; }

    /// <summary>Profile icon path for <see cref="WriteTerminalFragment"/> (environment variables allowed).</summary>
    public string? FragmentIcon { get; set; }

    public List<string> Errors { get; } = [];

    public static PickleOptions Parse(IReadOnlyList<string> args)
    {
        var o = new PickleOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next()
            {
                if (i + 1 < args.Count)
                {
                    return args[++i];
                }

                o.Errors.Add($"Missing value for {a}");
                return null;
            }

            switch (a.ToLowerInvariant())
            {
                case "-c":
                case "-command":
                case "--command":
                    o.Command = string.Join(' ', args.Skip(i + 1));
                    i = args.Count;
                    break;
                case "-f":
                case "-file":
                case "--file":
                    o.File = Next();
                    o.FileArguments = [.. args.Skip(i + 1)];
                    i = args.Count;
                    break;
                case "--headless":
                    o.Headless = true;
                    break;
                case "-noprofile":
                case "--no-profile":
                    o.NoProfile = true;
                    break;
                case "--no-plugins":
                    o.NoPlugins = true;
                    break;
                case "-nologo":
                case "--no-logo":
                    o.NoLogo = true;
                    break;
                case "-v":
                case "--version":
                case "-version":
                    o.ShowVersion = true;
                    break;
                case "-h":
                case "-?":
                case "--help":
                case "-help":
                    o.ShowHelp = true;
                    break;
                case "--log-level":
                    o.LogLevel = Next();
                    break;
                case "--elevated-helper":
                    o.ElevatedHelperPipe = Next();
                    o.ElevatedHelperNonce = Next();
                    break;
                case "--install-terminal-profile":
                    o.InstallTerminalProfile = true;
                    break;
                case "--uninstall-terminal-profile":
                    o.UninstallTerminalProfile = true;
                    break;
                case "--write-terminal-fragment":
                    o.WriteTerminalFragment = Next();
                    break;
                case "--fragment-commandline":
                    o.FragmentCommandLine = Next();
                    break;
                case "--fragment-icon":
                    o.FragmentIcon = Next();
                    break;
                default:
                    if (!a.StartsWith('-') && o.File is null && a.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                    {
                        o.File = a;
                        o.FileArguments = [.. args.Skip(i + 1)];
                        i = args.Count;
                    }
                    else
                    {
                        o.Errors.Add($"Unknown argument '{a}'");
                    }

                    break;
            }
        }

        return o;
    }

    public const string HelpText = """
        Pickle — a PowerShell 7 shell with superpowers

        Usage:
          pickle                         Start the interactive shell
          pickle -c <command...>         Run a command and exit
          pickle <script.ps1> [args]     Run a script and exit
          pickle -f <script.ps1> [args]  Run a script and exit

        Options:
          --no-profile                   Don't load profile.ps1
          --no-plugins                   Don't load third-party plugins
          --no-logo                      Hide the startup banner
          --headless                     Read commands from stdin without the interactive UI
          --log-level <level>            trace|debug|info|warning|error (logs in the data dir)
          --install-terminal-profile     Add Pickle to Windows Terminal and exit
          --uninstall-terminal-profile   Remove Pickle from Windows Terminal and exit
          -v, --version                  Print version
          -h, --help                     Show this help

        Inside Pickle, run `pk help` for built-in commands and press F1 for the command palette.
        """;
}
