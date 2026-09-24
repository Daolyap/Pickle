using Pickle.Abstractions;

namespace Pickle.Core.Translation.Rewriters;

/// <summary>How a command name resolves in the session (CommandType name and, for applications, the file path).</summary>
public sealed record CommandLookup(string CommandType, string? Path)
{
    public bool IsApplication => CommandType == "Application";
}

/// <summary>
/// Windows only. <c>sudo &lt;native exe&gt;</c> is left to Windows' built-in <c>sudo.exe</c> when it exists. Anything else
/// (PowerShell commands, or natives without sudo.exe) runs in a new elevated Pickle window through UAC:
/// <c>Start-Process -Verb RunAs pickle -c "Set-Location …; try { cmd } finally { Read-Host … }"</c> — the window stays
/// open until Enter so the output can be read. Plain <c>sudo</c>, <c>sudo -i</c>, <c>sudo -s</c>, <c>sudo su</c> open an
/// elevated interactive Pickle. A user-provided <c>sudo</c> (gsudo, a function) is never overridden.
/// </summary>
public sealed class SudoRewriter : IInputRewriter
{
    private readonly bool _isWindows;
    private readonly Func<string, CommandLookup?> _lookup;
    private readonly string? _builtInSudo;
    private readonly string _picklePath;

    public SudoRewriter(bool isWindows, Func<string, CommandLookup?> lookup, string? builtInSudo, string picklePath)
    {
        _isWindows = isWindows;
        _lookup = lookup;
        _builtInSudo = builtInSudo;
        _picklePath = picklePath;
    }

    public string Name => "sudo";

    public int Order => 60;

    public static string? FindBuiltInSudo()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var path = string.IsNullOrEmpty(system) ? null : Path.Combine(system, "sudo.exe");
        return path is not null && File.Exists(path) ? path : null;
    }

    public RewriteResult? Rewrite(string input, RewriteContext context)
    {
        if (!_isWindows || !input.Contains("sudo", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var edits = new TextEdits();
        foreach (var statement in ShellLexer.Statements(input))
        {
            var words = statement.Segments[0].Words;
            if (words.Count == 0 || words[0].Literal != "sudo")
            {
                continue;
            }

            var index = 1;
            while (index < words.Count && words[index].Literal is "-E" or "-H" or "--preserve-env")
            {
                index++;
            }

            var sudo = _lookup("sudo");
            var userSudo = sudo is not null && !(sudo.IsApplication && string.Equals(sudo.Path, _builtInSudo, StringComparison.OrdinalIgnoreCase));
            if (userSudo)
            {
                return null;
            }

            if (index >= words.Count || words[index].Literal is "-i" or "-s" or "su" or "-")
            {
                edits.Replace(statement.Start, statement.End, $"Start-Process -Verb RunAs -FilePath {PowerShellText.SingleQuote(_picklePath)}");
                continue;
            }

            if (words[index].Text.StartsWith('-'))
            {
                return null;
            }

            var target = _lookup(words[index].Literal ?? words[index].Text);
            if (target is { IsApplication: true } && _builtInSudo is not null)
            {
                continue;
            }

            var command = input[words[index].Start..statement.End];
            var script = $"Set-Location -LiteralPath {PowerShellText.SingleQuote(context.Cwd)}; try {{ {command} }} finally {{ Read-Host 'Press Enter to close' }}";
            var arguments = "-NoLogo -c " + PowerShellText.WindowsArgument(script);
            edits.Replace(
                statement.Start,
                statement.End,
                $"Start-Process -Verb RunAs -FilePath {PowerShellText.SingleQuote(_picklePath)} -ArgumentList {PowerShellText.SingleQuote(arguments)}");
        }

        return edits.Count == 0 ? null : new RewriteResult(edits.Apply(input), "sudo runs the command in an elevated Pickle window (UAC)");
    }
}
