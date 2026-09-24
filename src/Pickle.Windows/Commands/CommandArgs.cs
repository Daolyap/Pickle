namespace Pickle.Windows.Commands;

/// <summary>Minimal <c>pk</c> argument parser: positionals, <c>--flag</c>, <c>--name value</c> / <c>--name=value</c>, <c>-y</c>.</summary>
internal sealed class CommandArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];

    public string? Error { get; private set; }

    public static CommandArgs Parse(IReadOnlyList<string> args, params string[] valueOptions)
    {
        var result = new CommandArgs();
        var takesValue = new HashSet<string>(valueOptions, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                result.Positional.AddRange(args.Skip(i + 1));
                break;
            }

            var isLong = arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2;
            var isShort = !isLong && arg.Length == 2 && arg[0] == '-' && char.IsLetter(arg[1]);
            if (!isLong && !isShort)
            {
                result.Positional.Add(arg);
                continue;
            }

            var name = arg.TrimStart('-');
            string? value = null;
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }
            else if (takesValue.Contains(name))
            {
                if (i + 1 >= args.Count)
                {
                    result.Error ??= $"--{name} needs a value.";
                    continue;
                }

                value = args[++i];
            }

            result._options[name] = value;
        }

        return result;
    }

    public bool Has(params string[] names) => names.Any(_options.ContainsKey);

    public string? Value(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public string? Arg(int index) => index < Positional.Count ? Positional[index] : null;

    public string Rest(int from) => string.Join(' ', Positional.Skip(from));

    public bool Yes => Has("yes", "y");
}
