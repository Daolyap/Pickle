using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Pickle.Wizards;

/// <summary>
/// A composite value such as <c>[{bindAddress}:]{localPort}:{remoteHost}:{remotePort}</c>: <c>{name}</c> is a
/// sub-field (value key <c>optionId.name</c>), <c>[...]</c> is emitted only when all its sub-fields are filled, and
/// <c>\[ \] \{ \} \\</c> escape. Anything else is literal, so PowerShell source like <c>@{LogName={log}}</c> works.
/// </summary>
public sealed class WizardTemplate
{
    private static readonly ConcurrentDictionary<string, WizardTemplate> Cache = new(StringComparer.Ordinal);
    private static readonly Regex PlaceholderRegex = new(@"\G\{([A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant);

    private const string RawLiteralPattern = @"'(?:[^'\u2018\u2019\u201A\u201B]|['\u2018\u2019\u201A\u201B]{2})*'|[^\s;'""{}()=,]+(?:\s*,\s*[^\s;'""{}()=,]+)*";

    private readonly List<Segment> _segments;
    private readonly Regex[] _matchers = new Regex[2];

    private WizardTemplate(string text, List<Segment> segments)
    {
        Text = text;
        _segments = segments;
        var all = new List<string>();
        var required = new List<string>();
        foreach (var segment in segments)
        {
            if (segment.Kind == SegmentKind.Placeholder)
            {
                all.Add(segment.Text);
                required.Add(segment.Text);
            }
            else if (segment.Kind == SegmentKind.Optional)
            {
                all.AddRange(segment.Children!.Where(c => c.Kind == SegmentKind.Placeholder).Select(c => c.Text));
            }
        }

        Placeholders = all;
        RequiredPlaceholders = required;
    }

    public string Text { get; }

    public IReadOnlyList<string> Placeholders { get; }

    /// <summary>Sub-fields outside optional groups.</summary>
    public IReadOnlyList<string> RequiredPlaceholders { get; }

    public static WizardTemplate Get(string template) => Cache.GetOrAdd(template, Parse);

    /// <exception cref="FormatException">Unbalanced or nested optional groups.</exception>
    public static WizardTemplate Parse(string template)
    {
        var root = new List<Segment>();
        List<Segment>? optional = null;
        var literal = new StringBuilder();

        void Flush()
        {
            if (literal.Length > 0)
            {
                (optional ?? root).Add(new Segment(SegmentKind.Literal, literal.ToString(), null));
                literal.Clear();
            }
        }

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '\\' && i + 1 < template.Length && template[i + 1] is '[' or ']' or '{' or '}' or '\\')
            {
                literal.Append(template[++i]);
            }
            else if (c == '[')
            {
                if (optional is not null)
                {
                    throw new FormatException($"Nested optional group in template '{template}'.");
                }

                Flush();
                optional = [];
            }
            else if (c == ']')
            {
                if (optional is null)
                {
                    throw new FormatException($"Unbalanced ']' in template '{template}'.");
                }

                Flush();
                root.Add(new Segment(SegmentKind.Optional, string.Empty, optional));
                optional = null;
            }
            else if (c == '{' && PlaceholderRegex.Match(template, i) is { Success: true } m)
            {
                Flush();
                (optional ?? root).Add(new Segment(SegmentKind.Placeholder, m.Groups[1].Value, null));
                i += m.Length - 1;
            }
            else
            {
                literal.Append(c);
            }
        }

        if (optional is not null)
        {
            throw new FormatException($"Unclosed '[' in template '{template}'.");
        }

        Flush();
        return new WizardTemplate(template, root);
    }

    /// <summary>
    /// Builds the value, or null when a required sub-field is empty. <paramref name="raw"/> formats sub-values as
    /// PowerShell literals (the template text itself is PowerShell source).
    /// </summary>
    public string? Render(Func<string, string?> getValue, bool raw)
    {
        var sb = new StringBuilder();
        foreach (var segment in _segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Literal:
                    sb.Append(segment.Text);
                    break;
                case SegmentKind.Placeholder:
                    var value = getValue(segment.Text);
                    if (string.IsNullOrEmpty(value))
                    {
                        return null;
                    }

                    sb.Append(raw ? PowerShellQuoting.ToExpressionLiteral(value) : value);
                    break;
                default:
                    var children = segment.Children!;
                    var values = children.Where(c => c.Kind == SegmentKind.Placeholder)
                        .Select(c => getValue(c.Text))
                        .ToList();
                    if (values.Any(string.IsNullOrEmpty))
                    {
                        break;
                    }

                    foreach (var child in children)
                    {
                        sb.Append(child.Kind == SegmentKind.Literal
                            ? child.Text
                            : raw ? PowerShellQuoting.ToExpressionLiteral(getValue(child.Text)!) : getValue(child.Text));
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Splits a value back into sub-values, or null when it doesn't fit the template.</summary>
    public IReadOnlyDictionary<string, string>? Match(string value, bool raw)
    {
        var regex = _matchers[raw ? 1 : 0] ??= BuildRegex(raw);
        var match = regex.Match(value);
        if (!match.Success)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < Placeholders.Count; i++)
        {
            var group = match.Groups["p" + i];
            if (group.Success)
            {
                result[Placeholders[i]] = raw ? PowerShellQuoting.FromExpressionLiteral(group.Value) : group.Value;
            }
        }

        return result;
    }

    private Regex BuildRegex(bool raw)
    {
        var sb = new StringBuilder("^");
        var index = 0;

        void Append(Segment segment)
        {
            if (segment.Kind == SegmentKind.Literal)
            {
                sb.Append(raw ? LiteralPatternLenient(segment.Text) : Regex.Escape(segment.Text));
            }
            else
            {
                sb.Append("(?<p").Append(index++).Append('>').Append(raw ? RawLiteralPattern : ".+?").Append(')');
            }
        }

        foreach (var segment in _segments)
        {
            if (segment.Kind == SegmentKind.Optional)
            {
                sb.Append("(?:");
                foreach (var child in segment.Children!)
                {
                    Append(child);
                }

                sb.Append(")?");
            }
            else
            {
                Append(segment);
            }
        }

        sb.Append('$');
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline | (raw ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(sb.ToString(), options, TimeSpan.FromSeconds(1));
    }

    // PowerShell source tolerates whitespace around punctuation (@{ Id = 1 }), so a typed hashtable still matches.
    private static string LiteralPatternLenient(string literal)
    {
        var sb = new StringBuilder(@"\s*");
        var words = literal.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var w = 0; w < words.Length; w++)
        {
            var word = words[w];
            if (w > 0)
            {
                sb.Append(char.IsLetterOrDigit(words[w - 1][^1]) && char.IsLetterOrDigit(word[0]) ? @"\s+" : @"\s*");
            }

            for (var i = 0; i < word.Length; i++)
            {
                if (i > 0 && !(char.IsLetterOrDigit(word[i - 1]) && char.IsLetterOrDigit(word[i])))
                {
                    sb.Append(@"\s*");
                }

                sb.Append(Regex.Escape(word[i].ToString()));
            }
        }

        return sb.Append(@"\s*").ToString();
    }

    private enum SegmentKind
    {
        Literal,
        Placeholder,
        Optional,
    }

    private sealed record Segment(SegmentKind Kind, string Text, List<Segment>? Children);
}
