namespace Neadocs.Engine.Infrastructure.Text;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public static class PipelineHash
{
    public static string Of(IReadOnlyList<RuleOperation> pipeline) => Of(pipeline, null, 0);

    /// <summary>
    /// The identity of everything that determines a chunk's <c>tsv_folded</c>.
    /// </summary>
    /// <remarks>
    /// <b>The search configuration and the prefix length belong in here, not alongside it.</b>
    /// This hash is stored per chunk as <c>normalizer_hash</c> and is the only thing that reports a
    /// chunk as stale, so a setting that changes the indexed vector but not the hash produces a
    /// column holding two incompatible indexing schemes with nothing anywhere saying so — search
    /// half-working, <c>/stats</c> reporting zero stale chunks, and no reason to reindex.
    /// </remarks>
    public static string Of(IReadOnlyList<RuleOperation> pipeline, string? searchConfig, int stemPrefixLength)
    {
        StringBuilder canonical = new();
        canonical.Append(Canonicalize(pipeline));

        // Appended rather than folded into the array so an existing rule set that names neither
        // keeps the hash it already had — otherwise adopting this change would mark every chunk in
        // every locale stale, including the ones whose indexing did not move.
        if (!string.IsNullOrEmpty(searchConfig))
        {
            canonical.Append("|cfg=").Append(searchConfig);
        }

        if (stemPrefixLength > 0)
        {
            canonical.Append("|pfx=").Append(stemPrefixLength.ToString(CultureInfo.InvariantCulture));
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()), hash);

        return Convert.ToHexStringLower(hash);
    }

    public static string Canonicalize(IReadOnlyList<RuleOperation> pipeline)
    {
        StringBuilder builder = new();
        builder.Append('[');

        for (int i = 0; i < pipeline.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            AppendOperation(builder, pipeline[i]);
        }

        builder.Append(']');

        return builder.ToString();
    }

    private static void AppendOperation(StringBuilder builder, RuleOperation operation)
    {
        SortedDictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["op"] = Quote(operation.Op),
        };

        if (operation.Form is not null)
        {
            fields["form"] = Quote(operation.Form);
        }

        if (operation.Mode is not null)
        {
            fields["mode"] = Quote(operation.Mode);
        }

        if (operation.Map is not null)
        {
            fields["map"] = CanonicalMap(operation.Map);
        }

        if (operation.Categories is not null)
        {
            fields["categories"] = CanonicalArray(operation.Categories);
        }

        if (operation.Tokens is not null)
        {
            fields["tokens"] = CanonicalArray(operation.Tokens);
        }

        builder.Append('{');

        bool first = true;

        foreach (KeyValuePair<string, string> field in fields)
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append(Quote(field.Key)).Append(':').Append(field.Value);
            first = false;
        }

        builder.Append('}');
    }

    private static string CanonicalMap(IReadOnlyDictionary<string, string> map)
    {
        StringBuilder builder = new();
        builder.Append('{');

        bool first = true;

        foreach (KeyValuePair<string, string> entry in map.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append(Quote(entry.Key)).Append(':').Append(Quote(entry.Value));
            first = false;
        }

        builder.Append('}');

        return builder.ToString();
    }

    private static string CanonicalArray(IReadOnlyList<string> values)
    {
        StringBuilder builder = new();
        builder.Append('[');

        bool first = true;

        foreach (string value in values.OrderBy(v => v, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append(Quote(value));
            first = false;
        }

        builder.Append(']');

        return builder.ToString();
    }

    private static string Quote(string value)
    {
        StringBuilder builder = new(value.Length + 2);
        builder.Append('"');

        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');

        return builder.ToString();
    }
}
