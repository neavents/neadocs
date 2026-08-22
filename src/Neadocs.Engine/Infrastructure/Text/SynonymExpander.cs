namespace Neadocs.Engine.Infrastructure.Text;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Infrastructure.Configuration;

public sealed class SynonymExpander
{
    private readonly FrozenDictionary<string, IReadOnlyList<string[]>> _byLocale;
    private readonly FrozenDictionary<string, string[]> _fallbackChains;

    public SynonymExpander(IOptions<DocumentEngineOptions> options, NormalizerRegistry normalizers)
        : this(options.Value.Text, normalizers)
    {
    }

    public SynonymExpander(TextOptions text, NormalizerRegistry normalizers)
    {
        Dictionary<string, IReadOnlyList<string[]>> byLocale = [];

        foreach (KeyValuePair<string, List<SynonymGroupOptions>> entry in text.Synonyms)
        {
            string locale = LocaleTag.Normalize(entry.Key);
            List<string[]> groups = [];

            foreach (SynonymGroupOptions group in entry.Value)
            {
                string[] normalized = group.Terms
                    .Select(term => normalizers.Normalize(locale, term))
                    .Where(term => term.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (normalized.Length >= 2)
                {
                    groups.Add(normalized);
                }
            }

            if (groups.Count > 0)
            {
                byLocale[locale] = groups;
            }
        }

        _byLocale = byLocale.ToFrozenDictionary(StringComparer.Ordinal);

        Dictionary<string, string[]> chains = [];

        foreach (KeyValuePair<string, List<string>> entry in text.LocaleFallback)
        {
            chains[LocaleTag.Normalize(entry.Key)] =
                entry.Value.Select(LocaleTag.Normalize).ToArray();
        }

        _fallbackChains = chains.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public int GroupCount => _byLocale.Values.Sum(g => g.Count);

    public IReadOnlyList<string> LocaleChain(string? locale)
    {
        string primary = LocaleTag.Normalize(locale);

        if (primary.Length == 0)
        {
            return [];
        }

        List<string> chain = [primary];

        if (_fallbackChains.TryGetValue(primary, out string[]? fallbacks))
        {
            foreach (string fallback in fallbacks)
            {
                if (!chain.Contains(fallback, StringComparer.Ordinal))
                {
                    chain.Add(fallback);
                }
            }
        }

        return chain;
    }

    public IReadOnlyList<string> Expand(string? locale, string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return [];
        }

        List<string> tokens = Tokenize(normalizedQuery);
        List<string> additions = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string token in tokens)
        {
            seen.Add(token);
        }

        seen.Add(normalizedQuery);

        foreach (string locus in LocaleChain(locale))
        {
            if (!_byLocale.TryGetValue(locus, out IReadOnlyList<string[]>? groups))
            {
                continue;
            }

            foreach (string[] group in groups)
            {
                if (!group.Any(term => ContainsPhrase(tokens, term)))
                {
                    continue;
                }

                foreach (string term in group)
                {
                    if (!ContainsPhrase(tokens, term) && seen.Add(term))
                    {
                        additions.Add(term);
                    }
                }
            }
        }

        return additions;
    }

    internal static List<string> Tokenize(string text) =>
        [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    private static bool ContainsPhrase(List<string> tokens, string term)
    {
        List<string> phrase = Tokenize(term);

        if (phrase.Count == 0 || phrase.Count > tokens.Count)
        {
            return false;
        }

        for (int start = 0; start <= tokens.Count - phrase.Count; start++)
        {
            bool matched = true;

            for (int i = 0; i < phrase.Count; i++)
            {
                if (!string.Equals(tokens[start + i], phrase[i], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
