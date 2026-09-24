using System.Diagnostics;
using Pickle.Abstractions;
using Pickle.Core.Commands;

namespace Pickle.Core.Aliases;

/// <summary><c>pk alias add|rm|ls|show|edit</c>.</summary>
public sealed class AliasCommand : IPickleCommand
{
    private readonly AliasManager _manager;

    public AliasCommand(AliasManager manager) => _manager = manager;

    public string Name => "alias";

    public string Description => "Persistent aliases: simple, parameterized ({name}, {name=default}, {*}) or script";

    public string Usage =>
        "pk alias add <name> '<body>' [--kind simple|param|script] [--dir <glob>] [--machine <name>] [--description <text>] [--force] | rm <name> | ls | show <name> | edit";

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var verb = args.Count == 0 ? "ls" : args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToList();
        try
        {
            return ValueTask.FromResult(verb switch
            {
                "add" or "set" => Add(context, rest),
                "rm" or "remove" or "del" => RemoveAlias(context, rest),
                "ls" or "list" => List(context),
                "show" => Show(context, rest),
                "edit" => _manager.Edit(context.WriteHost) ? 0 : 1,
                "-h" or "--help" or "help" => Help(context),
                _ => Fail(context, $"Unknown subcommand '{args[0]}'. Usage: {Usage}"),
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Fail(context, ex.Message));
        }
    }

    private int Add(PickleCommandContext context, List<string> args)
    {
        string? kind = null, dir = null, machine = null, description = null;
        var force = false;
        var positional = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? inline = null;
            var eq = arg.StartsWith("--", StringComparison.Ordinal) ? arg.IndexOf('=', StringComparison.Ordinal) : -1;
            if (eq > 0)
            {
                inline = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            string Value()
            {
                if (inline is not null)
                {
                    return inline;
                }

                return ++i < args.Count ? args[i] : throw new ArgumentException($"Missing value for {arg}.");
            }

            switch (arg)
            {
                case "--kind" or "-k":
                    kind = Value();
                    break;
                case "--dir" or "--directory" or "-d":
                    dir = Value();
                    break;
                case "--machine" or "-m":
                    machine = Value();
                    break;
                case "--description" or "--desc":
                    description = Value();
                    break;
                case "--force" or "-f":
                    force = true;
                    break;
                case "--":
                    positional.AddRange(args.Skip(i + 1));
                    i = args.Count;
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count < 2)
        {
            return Fail(context, "Usage: pk alias add <name> '<body>' (quote the body so PowerShell passes it through unchanged)");
        }

        var body = string.Join(' ', positional.Skip(1));
        var alias = new AliasDefinition
        {
            Name = positional[0],
            Body = body,
            Kind = kind is null ? AliasCompiler.DetectKind(body) : ParseKind(kind),
            DirectoryScope = dir,
            MachineScope = machine,
            Description = description,
        };

        _manager.SetChecked(alias, force);
        var scope = (dir is null ? string.Empty : $" in {dir}") + (machine is null ? string.Empty : $" on {machine}");
        context.WriteHost($"Alias {alias.Name} ({alias.Kind.ToString().ToLowerInvariant()}){scope}: {alias.Body}");
        return 0;
    }

    public static AliasKind ParseKind(string kind) => kind.ToLowerInvariant() switch
    {
        "simple" => AliasKind.Simple,
        "param" or "parameterized" or "params" => AliasKind.Parameterized,
        "script" or "function" => AliasKind.Script,
        _ => throw new ArgumentException($"Unknown alias kind '{kind}' (simple, param, script)."),
    };

    private int RemoveAlias(PickleCommandContext context, List<string> args)
    {
        if (args.Count == 0)
        {
            return Fail(context, "Usage: pk alias rm <name>");
        }

        var failed = 0;
        foreach (var name in args)
        {
            if (_manager.Remove(name))
            {
                context.WriteHost($"Removed alias {name}.");
            }
            else
            {
                context.WriteError($"No alias named '{name}'.");
                failed++;
            }
        }

        return failed == 0 ? 0 : 1;
    }

    private int List(PickleCommandContext context)
    {
        foreach (var alias in _manager.All)
        {
            context.WriteObject(alias);
        }

        return 0;
    }

    private int Show(PickleCommandContext context, List<string> args)
    {
        if (args.Count == 0 || _manager.Get(args[0]) is not { } alias)
        {
            return Fail(context, args.Count == 0 ? "Usage: pk alias show <name>" : $"No alias named '{args[0]}'.");
        }

        context.WriteHost(AliasCompiler.BuildFunction(alias));
        return 0;
    }

    private int Help(PickleCommandContext context)
    {
        context.WriteHost(Usage);
        context.WriteHost("  pk alias add ll 'Get-ChildItem -Force'");
        context.WriteHost("  pk alias add gco 'git checkout {branch}'          # gco main → git checkout main");
        context.WriteHost("  pk alias add serve 'python -m http.server {port=8000}'");
        context.WriteHost("  pk alias add build 'dotnet build' --dir '~/src/app/**'");
        return 0;
    }

    private static int Fail(PickleCommandContext context, string message)
    {
        context.WriteError(message);
        return 1;
    }
}

/// <summary>Opens a file in $VISUAL/$EDITOR, VS Code (--wait), notepad on Windows or nano/vi, and waits for it to close.</summary>
internal static class EditorProcess
{
    public static int Run(string file)
    {
        var (exe, args) = Resolve() ?? throw new InvalidOperationException("No editor found. Set $env:EDITOR (or $env:VISUAL).");
        var psi = new ProcessStartInfo { UseShellExecute = false };
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = ExecutableLocator.SystemProgram("cmd.exe");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
        }
        else
        {
            psi.FileName = exe;
        }

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add(file);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start editor '{exe}'.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static (string Exe, List<string> Args)? Resolve()
    {
        foreach (var variable in new[] { "VISUAL", "EDITOR" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                var parts = SplitCommandLine(value);
                if (parts.Count > 0 && ExecutableLocator.Find(parts[0]) is { } exe)
                {
                    return (exe, parts.Skip(1).ToList());
                }
            }
        }

        if (ExecutableLocator.Find("code") is { } code)
        {
            return (code, ["--wait"]);
        }

        if (OperatingSystem.IsWindows())
        {
            return (ExecutableLocator.SystemProgram("notepad.exe"), []);
        }

        return (ExecutableLocator.Find("nano") ?? ExecutableLocator.Find("vi")) is { } terminal ? (terminal, []) : null;
    }

    private static List<string> SplitCommandLine(string value)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var c in value)
        {
            if (quote is null && c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == quote)
            {
                quote = null;
            }
            else if (quote is null && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts.Count == 0 ? [value] : parts;
    }
}
