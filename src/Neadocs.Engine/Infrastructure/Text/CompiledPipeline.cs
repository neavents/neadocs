namespace Neadocs.Engine.Infrastructure.Text;

using System;
using System.Collections.Generic;
using System.Text;

public sealed class CompiledPipeline
{
    public CompiledPipeline(
        string tag,
        string hash,
        IReadOnlyList<ICompiledOperation> operations,
        IReadOnlyList<SelfTestCase> selfTests)
        : this(tag, hash, operations, selfTests, RuleOperations.DefaultSearchConfig, 0)
    {
    }

    public CompiledPipeline(
        string tag,
        string hash,
        IReadOnlyList<ICompiledOperation> operations,
        IReadOnlyList<SelfTestCase> selfTests,
        string searchConfig,
        int stemPrefixLength)
    {
        Tag = tag;
        Hash = hash;
        Operations = operations;
        SelfTests = selfTests;
        SearchConfig = searchConfig;
        StemPrefixLength = stemPrefixLength;
    }

    public string Tag { get; }

    public string Hash { get; }

    public IReadOnlyList<ICompiledOperation> Operations { get; }

    public IReadOnlyList<SelfTestCase> SelfTests { get; }

    /// <summary>The Postgres <c>regconfig</c> this locale's text is indexed and queried with.</summary>
    public string SearchConfig { get; }

    /// <summary>Length of the weight-D truncated copies, or zero when the locale wants none.</summary>
    public int StemPrefixLength { get; }

    public bool EmitsPrefixes => StemPrefixLength > 0;

    public string Normalize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        string current = input;

        foreach (ICompiledOperation operation in Operations)
        {
            current = operation.Apply(current);
        }

        return current;
    }

    /// <summary>
    /// The truncated copies of every sufficiently long token in already-normalized text.
    /// </summary>
    /// <remarks>
    /// Derived from the normalized string rather than produced by a pipeline operation, and that
    /// distinction is load-bearing. A pipeline operation would rewrite the one string used for
    /// <i>both</i> indexing and querying — and since <c>websearch_to_tsquery</c> ANDs the terms it
    /// finds, appending prefixes to a query would make it <b>stricter</b> (the document would have
    /// to contain the word and its own truncation), which is the exact opposite of the intent.
    /// <para>
    /// Kept separate, the two sides can use it as each needs: the index adds these as extra
    /// low-weight lexemes, and the query ORs a second tsquery built from them.
    /// </para>
    /// <para>
    /// Tokens shorter than or equal to the prefix length are skipped rather than emitted whole.
    /// Emitting them would duplicate a lexeme the full-weight vector already carries, at weight D,
    /// which only dilutes ranking.
    /// </para>
    /// </remarks>
    public string Prefixes(string? normalized)
    {
        if (!EmitsPrefixes || string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string token in normalized.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length <= StemPrefixLength)
            {
                continue;
            }

            string prefix = token[..StemPrefixLength];

            if (!seen.Add(prefix))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(prefix);
        }

        return builder.ToString();
    }
}
