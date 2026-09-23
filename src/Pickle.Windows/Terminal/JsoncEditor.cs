using System.Text.Json;

namespace Pickle.Windows.Terminal;

/// <summary>
/// Minimal, targeted edits to JSON-with-comments text (Windows Terminal's settings.json): only the bytes of one
/// root-level value change, so comments, ordering, trailing commas and formatting survive.
/// </summary>
public static class JsoncEditor
{
    private enum Kind
    {
        String,
        Literal,
        Open,
        Close,
        Colon,
        Comma,
    }

    /// <summary>The string value of a root-level property, or null when missing or not a string.</summary>
    public static string? GetRootString(string text, string key)
    {
        var tokens = Tokenize(text);
        var index = FindRootValue(text, tokens, key);
        if (index < 0 || tokens[index].Kind != Kind.String)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(text[tokens[index].Start..tokens[index].End]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sets a root-level property to a string, replacing its value in place or inserting it first.</summary>
    public static string SetRootString(string text, string key, string value)
    {
        var tokens = Tokenize(text);
        if (tokens.Count == 0 || tokens[0].Kind != Kind.Open || text[tokens[0].Start] != '{')
        {
            throw new FormatException("The settings file does not contain a JSON object.");
        }

        var encoded = JsonSerializer.Serialize(value);
        var index = FindRootValue(text, tokens, key);
        if (index >= 0)
        {
            var end = ValueEnd(tokens, index);
            return string.Concat(text.AsSpan(0, tokens[index].Start), encoded, text.AsSpan(end));
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var open = tokens[0].End;
        var firstMember = tokens.Count > 1 && tokens[1].Depth == 1 && tokens[1].Kind != Kind.Close ? tokens[1] : (Token?)null;
        var indent = firstMember is { } m ? LineIndent(text, m.Start) : "    ";
        var property = JsonSerializer.Serialize(key) + ": " + encoded;
        var insertion = firstMember is null
            ? newline + indent + property + newline
            : newline + indent + property + ",";
        return string.Concat(text.AsSpan(0, open), insertion, text.AsSpan(open));
    }

    private static int FindRootValue(string text, List<Token> tokens, string key)
    {
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == Kind.String && t.Depth == 1 && tokens[i + 1].Kind == Kind.Colon && KeyEquals(text, t, key))
            {
                return i + 2;
            }
        }

        return -1;
    }

    private static bool KeyEquals(string text, Token token, string key)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(text[token.Start..token.End]) == key;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int ValueEnd(List<Token> tokens, int index)
    {
        var value = tokens[index];
        if (value.Kind != Kind.Open)
        {
            return value.End;
        }

        for (var i = index + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == Kind.Close && tokens[i].Depth == value.Depth)
            {
                return tokens[i].End;
            }
        }

        throw new FormatException("Unterminated object or array in settings file.");
    }

    private static string LineIndent(string text, int position)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        var i = lineStart;
        while (i < position && text[i] is ' ' or '\t')
        {
            i++;
        }

        return i == position ? text[lineStart..position] : "    ";
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var depth = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '﻿')
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var newline = text.IndexOf('\n', i);
                i = newline < 0 ? text.Length : newline + 1;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
                continue;
            }

            var start = i;
            switch (c)
            {
                case '"':
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        i += text[i] == '\\' ? 2 : 1;
                    }

                    i = Math.Min(i + 1, text.Length);
                    tokens.Add(new Token(Kind.String, start, i, depth));
                    break;
                case '{' or '[':
                    tokens.Add(new Token(Kind.Open, start, ++i, depth));
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    tokens.Add(new Token(Kind.Close, start, ++i, depth));
                    break;
                case ':':
                    tokens.Add(new Token(Kind.Colon, start, ++i, depth));
                    break;
                case ',':
                    tokens.Add(new Token(Kind.Comma, start, ++i, depth));
                    break;
                default:
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (',' or ':' or '{' or '}' or '[' or ']' or '"' or '/'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(Kind.Literal, start, Math.Max(i, start + 1), depth));
                    i = Math.Max(i, start + 1);
                    break;
            }
        }

        return tokens;
    }

    private readonly record struct Token(Kind Kind, int Start, int End, int Depth);
}
