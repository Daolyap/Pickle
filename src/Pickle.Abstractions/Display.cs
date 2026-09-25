using System.Management.Automation;

namespace Pickle.Abstractions;

/// <summary>A column of a <see cref="Display.Columns"/> view: a property, a renamed property, or a computed value.</summary>
public readonly record struct DisplayColumn(string Header, string? Property, object? Value)
{
    public static implicit operator DisplayColumn(string property) => new(property, property, null);

    public static DisplayColumn Alias(string header, string property) => new(header, property, null);

    public static DisplayColumn Note(string header, object? value) => new(header, null, value);
}

public static class Display
{
    /// <summary>
    /// Wraps <paramref name="item"/> so PowerShell's default formatting shows just these columns (a table for up to four),
    /// while the object keeps all its properties for scripts (<c>(pk winget list).Id</c>).
    /// </summary>
    public static PSObject Columns(object item, params DisplayColumn[] columns)
    {
        var pso = new PSObject(item);
        foreach (var column in columns)
        {
            if (column.Property is null)
            {
                pso.Properties.Add(new PSNoteProperty(column.Header, column.Value));
            }
            else if (column.Property != column.Header)
            {
                pso.Properties.Add(new PSAliasProperty(column.Header, column.Property));
            }
        }

        pso.Members.Add(new PSMemberSet("PSStandardMembers", [new PSPropertySet("DefaultDisplayPropertySet", [.. columns.Select(c => c.Header)])]));
        return pso;
    }

    /// <summary>The item behind a <see cref="Columns"/> wrapper (or the object itself).</summary>
    public static object? Unwrap(object? value) => value is PSObject pso ? pso.BaseObject : value;
}
