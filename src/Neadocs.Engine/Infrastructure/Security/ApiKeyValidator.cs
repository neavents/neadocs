namespace Neadocs.Engine.Infrastructure.Security;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Neadocs.Engine.Infrastructure.Configuration;

public sealed class ApiKeyValidator
{
    public const string HeaderName = "X-Project-Key";

    private readonly List<Entry> _entries;

    public ApiKeyValidator(DocumentEngineOptions options)
        : this(options.AllowedProjectKeys)
    {
    }

    public ApiKeyValidator(string allowedProjectKeys)
    {
        _entries = Parse(allowedProjectKeys);
    }

    public int Count => _entries.Count;

    public bool TryResolve(string? presentedKey, out string tenant, out DocumentScope scopes)
    {
        tenant = string.Empty;
        scopes = DocumentScope.None;

        if (string.IsNullOrEmpty(presentedKey) || _entries.Count == 0)
        {
            return false;
        }

        byte[] presented = Encoding.UTF8.GetBytes(presentedKey);
        bool matched = false;

        foreach (Entry entry in _entries)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, entry.Key) && !matched)
            {
                tenant = entry.Tenant;
                scopes = entry.Scopes;
                matched = true;
            }
        }

        return matched;
    }

    private static List<Entry> Parse(string configured)
    {
        List<Entry> entries = [];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return entries;
        }

        foreach (string raw in configured.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string pair = raw.Trim();

            if (pair.Length == 0)
            {
                continue;
            }

            int firstSeparator = pair.IndexOf(':');

            if (firstSeparator <= 0 || firstSeparator == pair.Length - 1)
            {
                throw new InvalidOperationException(
                    $"DocumentEngine:AllowedProjectKeys entry '{pair}' is malformed. "
                    + "Expected 'tenant:key' or 'tenant:key:scopes'.");
            }

            string tenant = pair[..firstSeparator].Trim();
            string remainder = pair[(firstSeparator + 1)..];

            int secondSeparator = remainder.IndexOf(':');
            string key;
            DocumentScope scopes;

            if (secondSeparator < 0)
            {
                key = remainder.Trim();
                scopes = DocumentScope.Admin;
            }
            else
            {
                key = remainder[..secondSeparator].Trim();
                string scopeText = remainder[(secondSeparator + 1)..].Trim();
                scopes = ParseScopeSuffix(scopeText, pair);
            }

            if (tenant.Length == 0 || key.Length == 0)
            {
                throw new InvalidOperationException(
                    $"DocumentEngine:AllowedProjectKeys entry '{pair}' has an empty tenant or key.");
            }

            entries.Add(new Entry(tenant, Encoding.UTF8.GetBytes(key), scopes));
        }

        return entries;
    }

    private static DocumentScope ParseScopeSuffix(string scopeText, string pair)
    {
        DocumentScope scopes = DocumentScope.None;

        foreach (string part in scopeText.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = part.Trim();

            DocumentScope parsed = name switch
            {
                _ when Equals(name, "read") => DocumentScope.Read,
                _ when Equals(name, "write") => DocumentScope.Write,
                _ when Equals(name, "admin") => DocumentScope.Admin,
                _ => DocumentScopeNames.Parse(name),
            };

            if (parsed == DocumentScope.None)
            {
                throw new InvalidOperationException(
                    $"DocumentEngine:AllowedProjectKeys entry '{pair}' names an unknown scope "
                    + $"'{name}'. Valid scopes are read, write and admin.");
            }

            scopes |= parsed;
        }

        if (scopes == DocumentScope.None)
        {
            throw new InvalidOperationException(
                $"DocumentEngine:AllowedProjectKeys entry '{pair}' declares an empty scope list. "
                + "Omit the third segment entirely to grant admin.");
        }

        return scopes;
    }

    private static bool Equals(string value, string name) =>
        string.Equals(value, name, StringComparison.OrdinalIgnoreCase);

    private readonly record struct Entry(string Tenant, byte[] Key, DocumentScope Scopes);
}
