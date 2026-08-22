namespace Neadocs.Engine.Infrastructure.Security;

using System;
using System.Collections.Generic;

[Flags]
public enum DocumentScope
{
    None = 0,
    Read = 1,
    Write = 2,
    Admin = 4,
}

public static class DocumentScopeNames
{
    public const string Read = "docs:read";
    public const string Write = "docs:write";
    public const string Admin = "docs:admin";

    public static readonly string[] All = [Read, Write, Admin];

    public static DocumentScope Parse(string? value) => value switch
    {
        null => DocumentScope.None,
        _ when Matches(value, Read) => DocumentScope.Read,
        _ when Matches(value, Write) => DocumentScope.Write,
        _ when Matches(value, Admin) => DocumentScope.Admin,
        _ => DocumentScope.None,
    };

    public static DocumentScope ParseDelimited(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DocumentScope.None;
        }

        DocumentScope granted = DocumentScope.None;

        foreach (string part in value.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            granted |= Parse(part.Trim());
        }

        return granted;
    }

    public static DocumentScope ParseMany(IEnumerable<string> values)
    {
        DocumentScope granted = DocumentScope.None;

        foreach (string value in values)
        {
            granted |= ParseDelimited(value);
        }

        return granted;
    }

    public static string Format(DocumentScope scope)
    {
        List<string> names = [];

        if (scope.HasFlag(DocumentScope.Admin))
        {
            names.Add(Admin);
        }

        if (scope.HasFlag(DocumentScope.Write))
        {
            names.Add(Write);
        }

        if (scope.HasFlag(DocumentScope.Read))
        {
            names.Add(Read);
        }

        return names.Count == 0 ? string.Empty : string.Join(" ", names);
    }

    private static bool Matches(string value, string name) =>
        string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
}

public static class DocumentScopeExtensions
{
    public static bool Grants(this DocumentScope held, DocumentScope required)
    {
        if (required == DocumentScope.None)
        {
            return true;
        }

        return Expand(held).HasFlag(required);
    }

    public static DocumentScope Expand(this DocumentScope held)
    {
        DocumentScope expanded = held;

        if (expanded.HasFlag(DocumentScope.Admin))
        {
            expanded |= DocumentScope.Write;
        }

        if (expanded.HasFlag(DocumentScope.Write))
        {
            expanded |= DocumentScope.Read;
        }

        return expanded;
    }
}
