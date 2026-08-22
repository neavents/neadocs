namespace Neadocs.Engine.Infrastructure.Text;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public interface ICompiledOperation
{
    string Name { get; }

    string Apply(string input);
}

public sealed class NormalizeFormOperation : ICompiledOperation
{
    private readonly NormalizationForm _form;

    public NormalizeFormOperation(NormalizationForm form) => _form = form;

    public string Name => RuleOperations.NormalizeForm;

    public string Apply(string input) =>
        input.Length == 0 || input.IsNormalized(_form) ? input : input.Normalize(_form);
}

public sealed class MapCharsOperation : ICompiledOperation
{
    private const int AsciiLimit = 128;

    private readonly string?[] _ascii = new string?[AsciiLimit];
    private readonly FrozenDictionary<char, string> _wide;

    public MapCharsOperation(IReadOnlyDictionary<char, string> map)
    {
        Dictionary<char, string> wide = [];

        foreach (KeyValuePair<char, string> entry in map)
        {
            if (entry.Key < AsciiLimit)
            {
                _ascii[entry.Key] = entry.Value;
            }
            else
            {
                wide[entry.Key] = entry.Value;
            }
        }

        _wide = wide.ToFrozenDictionary();
    }

    public string Name => RuleOperations.MapChars;

    public string Apply(string input)
    {
        int first = -1;

        for (int i = 0; i < input.Length; i++)
        {
            if (Lookup(input[i]) is not null)
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return input;
        }

        StringBuilder builder = new(input.Length);
        builder.Append(input, 0, first);

        for (int i = first; i < input.Length; i++)
        {
            char c = input[i];
            string? replacement = Lookup(c);

            if (replacement is null)
            {
                builder.Append(c);
            }
            else
            {
                builder.Append(replacement);
            }
        }

        return builder.ToString();
    }

    private string? Lookup(char c) =>
        c < AsciiLimit ? _ascii[c] : _wide.TryGetValue(c, out string? value) ? value : null;
}

public sealed class MapSequencesOperation : ICompiledOperation
{
    private readonly FrozenDictionary<char, KeyValuePair<string, string>[]> _byFirstChar;
    private readonly int _longest;

    public MapSequencesOperation(IReadOnlyDictionary<string, string> map)
    {
        Dictionary<char, List<KeyValuePair<string, string>>> grouped = [];
        int longest = 0;

        foreach (KeyValuePair<string, string> entry in map)
        {
            if (entry.Key.Length == 0)
            {
                continue;
            }

            longest = Math.Max(longest, entry.Key.Length);

            if (!grouped.TryGetValue(entry.Key[0], out List<KeyValuePair<string, string>>? bucket))
            {
                bucket = [];
                grouped[entry.Key[0]] = bucket;
            }

            bucket.Add(entry);
        }

        Dictionary<char, KeyValuePair<string, string>[]> ordered = [];

        foreach (KeyValuePair<char, List<KeyValuePair<string, string>>> group in grouped)
        {
            group.Value.Sort(static (a, b) => b.Key.Length.CompareTo(a.Key.Length));
            ordered[group.Key] = [.. group.Value];
        }

        _byFirstChar = ordered.ToFrozenDictionary();
        _longest = longest;
    }

    public string Name => RuleOperations.MapSequences;

    public string Apply(string input)
    {
        if (_longest == 0 || input.Length == 0)
        {
            return input;
        }

        StringBuilder? builder = null;
        int i = 0;

        while (i < input.Length)
        {
            string? replacement = null;
            int matchedLength = 0;

            if (_byFirstChar.TryGetValue(input[i], out KeyValuePair<string, string>[]? candidates))
            {
                foreach (KeyValuePair<string, string> candidate in candidates)
                {
                    if (candidate.Key.Length <= input.Length - i
                        && string.CompareOrdinal(input, i, candidate.Key, 0, candidate.Key.Length) == 0)
                    {
                        replacement = candidate.Value;
                        matchedLength = candidate.Key.Length;
                        break;
                    }
                }
            }

            if (replacement is null)
            {
                builder?.Append(input[i]);
                i++;
                continue;
            }

            if (builder is null)
            {
                builder = new StringBuilder(input.Length);
                builder.Append(input, 0, i);
            }

            builder.Append(replacement);
            i += matchedLength;
        }

        return builder?.ToString() ?? input;
    }
}

public sealed class LowercaseOperation : ICompiledOperation
{
    public const string AsciiMode = "ascii";
    public const string InvariantMode = "invariant";

    private readonly bool _asciiOnly;

    public LowercaseOperation(bool asciiOnly) => _asciiOnly = asciiOnly;

    public string Name => RuleOperations.Lowercase;

    public string Apply(string input)
    {
        if (input.Length == 0)
        {
            return input;
        }

        if (!_asciiOnly)
        {
            return input.ToLowerInvariant();
        }

        int first = -1;

        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] is >= 'A' and <= 'Z')
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return input;
        }

        return string.Create(input.Length, (input, first), static (span, state) =>
        {
            (string source, int start) = state;
            source.AsSpan().CopyTo(span);

            for (int i = start; i < span.Length; i++)
            {
                if (span[i] is >= 'A' and <= 'Z')
                {
                    span[i] = (char)(span[i] + 32);
                }
            }
        });
    }
}

public sealed class StripUnicodeCategoryOperation : ICompiledOperation
{
    private readonly int _mask;

    public StripUnicodeCategoryOperation(IEnumerable<UnicodeCategory> categories)
    {
        int mask = 0;

        foreach (UnicodeCategory category in categories)
        {
            mask |= 1 << (int)category;
        }

        _mask = mask;
    }

    public string Name => RuleOperations.StripUnicodeCategory;

    public string Apply(string input)
    {
        if (_mask == 0 || input.Length == 0)
        {
            return input;
        }

        int first = -1;

        for (int i = 0; i < input.Length; i++)
        {
            if (ShouldStrip(input[i]))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return input;
        }

        StringBuilder builder = new(input.Length);
        builder.Append(input, 0, first);

        for (int i = first; i < input.Length; i++)
        {
            if (!ShouldStrip(input[i]))
            {
                builder.Append(input[i]);
            }
        }

        return builder.ToString();
    }

    private bool ShouldStrip(char c) =>
        (_mask & (1 << (int)CharUnicodeInfo.GetUnicodeCategory(c))) != 0;
}

public sealed class CollapseWhitespaceOperation : ICompiledOperation
{
    public string Name => RuleOperations.CollapseWhitespace;

    public string Apply(string input)
    {
        if (input.Length == 0)
        {
            return input;
        }

        StringBuilder builder = new(input.Length);
        bool pendingSpace = false;
        bool wrote = false;

        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = wrote;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
            wrote = true;
        }

        string result = builder.ToString();

        return string.Equals(result, input, StringComparison.Ordinal) ? input : result;
    }
}

/// <summary>
/// Removes whole tokens from the text — see <see cref="RuleOperations.DropTokens"/>.
/// </summary>
/// <remarks>
/// Matches on whole tokens only, never substrings. A substring rule would quietly eat the middle of
/// longer words: dropping <c>ne</c> from <c>nerede</c> leaves <c>rede</c>, and a stopword list is
/// the last place anyone would look for that.
/// <para>
/// Applied to the query and to the indexed text alike, because they are the same pipeline. A word
/// removed from one side and kept on the other would simply never match.
/// </para>
/// </remarks>
public sealed class DropTokensOperation : ICompiledOperation
{
    private readonly FrozenSet<string> _tokens;

    public DropTokensOperation(IEnumerable<string> tokens) =>
        _tokens = tokens.ToFrozenSet(StringComparer.Ordinal);

    public string Name => RuleOperations.DropTokens;

    public int Count => _tokens.Count;

    public string Apply(string input)
    {
        if (input.Length == 0 || _tokens.Count == 0)
        {
            return input;
        }

        StringBuilder builder = new(input.Length);
        bool wrote = false;
        int start = -1;

        for (int i = 0; i <= input.Length; i++)
        {
            bool boundary = i == input.Length || char.IsWhiteSpace(input[i]);

            if (!boundary)
            {
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (start >= 0)
            {
                ReadOnlySpan<char> token = input.AsSpan(start, i - start);

                if (!_tokens.Contains(token.ToString()))
                {
                    if (wrote)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(token);
                    wrote = true;
                }

                start = -1;
            }
        }

        string result = builder.ToString();

        return string.Equals(result, input, StringComparison.Ordinal) ? input : result;
    }
}
