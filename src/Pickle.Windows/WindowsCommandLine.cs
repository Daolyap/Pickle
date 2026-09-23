using System.Text;

namespace Pickle.Windows;

/// <summary>Builds Windows command lines that CommandLineToArgvW / the MSVC runtime split back into the same arguments.</summary>
public static class WindowsCommandLine
{
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    public static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"' or '\n' or '\v'))
        {
            return argument;
        }

        var sb = new StringBuilder(argument.Length + 2).Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                sb.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(argument[i]);
            }
        }

        return sb.Append('"').ToString();
    }
}
