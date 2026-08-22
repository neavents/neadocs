namespace Neadocs.Engine.Infrastructure.Text;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public sealed class RuleSetException : Exception
{
    public RuleSetException(string message) : base(message)
    {
    }
}

public static class PipelineCompiler
{
    public static CompiledPipeline Compile(RuleSet ruleSet, string origin)
    {
        if (string.IsNullOrWhiteSpace(ruleSet.Tag))
        {
            throw new RuleSetException($"{origin}: 'tag' must be set.");
        }

        string tag = ruleSet.Tag == RuleOperations.FallbackTag
            ? RuleOperations.FallbackTag
            : LocaleTag.Normalize(ruleSet.Tag);

        if (tag != RuleOperations.FallbackTag && !LocaleTag.IsWellFormed(tag))
        {
            throw new RuleSetException(
                $"{origin}: 'tag' is '{ruleSet.Tag}', which is neither a well-formed BCP-47 tag "
                + $"nor the fallback '{RuleOperations.FallbackTag}'.");
        }

        if (ruleSet.Pipeline.Count == 0)
        {
            throw new RuleSetException($"{origin} [{tag}]: 'pipeline' must declare at least one operation.");
        }

        if (ruleSet.SelfTest.Count < RuleOperations.MinimumSelfTests)
        {
            throw new RuleSetException(
                $"{origin} [{tag}]: 'selfTest' must declare at least "
                + $"{RuleOperations.MinimumSelfTests} cases; found {ruleSet.SelfTest.Count}. "
                + "A rule set without a proof of what 'working' means for it is not accepted.");
        }

        string searchConfig = ValidateSearchConfig(ruleSet.SearchConfig, tag, origin);
        int stemPrefixLength = ValidateStemPrefixLength(ruleSet.StemPrefixLength, tag, origin);

        List<ICompiledOperation> operations = [];

        for (int i = 0; i < ruleSet.Pipeline.Count; i++)
        {
            operations.Add(CompileOperation(ruleSet.Pipeline[i], tag, origin, i));
        }

        CompiledPipeline compiled = new(
            tag,
            PipelineHash.Of(ruleSet.Pipeline, ruleSet.SearchConfig, stemPrefixLength),
            operations,
            ruleSet.SelfTest,
            searchConfig,
            stemPrefixLength);

        RunSelfTest(compiled, origin);

        return compiled;
    }

    /// <summary>
    /// Rejects anything that is not a plain identifier.
    /// </summary>
    /// <remarks>
    /// The value is always sent to Postgres as a <i>parameter</i> cast to <c>regconfig</c>, never
    /// interpolated into SQL, so this is not the injection boundary. It is a legibility boundary:
    /// an unknown configuration name fails at query time with an error naming a type cast, on every
    /// search, long after whoever edited the rule set has moved on. Failing at load says which file
    /// is wrong.
    /// </remarks>
    private static string ValidateSearchConfig(string? value, string tag, string origin)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RuleOperations.DefaultSearchConfig;
        }

        string trimmed = value.Trim();

        foreach (char c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                throw new RuleSetException(
                    $"{origin} [{tag}]: 'searchConfig' is '{value}'. It must be a bare Postgres "
                    + "text search configuration name such as 'turkish' or 'simple' — letters, "
                    + "digits and underscores only.");
            }
        }

        return trimmed;
    }

    private static int ValidateStemPrefixLength(int value, string tag, string origin)
    {
        if (value == 0)
        {
            return 0;
        }

        if (value < RuleOperations.MinStemPrefixLength || value > RuleOperations.MaxStemPrefixLength)
        {
            throw new RuleSetException(
                $"{origin} [{tag}]: 'stemPrefixLength' is {value.ToString(CultureInfo.InvariantCulture)}; "
                + $"it must be 0 (off) or between {RuleOperations.MinStemPrefixLength} and "
                + $"{RuleOperations.MaxStemPrefixLength}. Shorter truncations are shared by most of "
                + "the vocabulary and stop discriminating; longer ones no longer reach past the "
                + "suffixes they exist to cut through.");
        }

        return value;
    }

    public static void RunSelfTest(CompiledPipeline pipeline, string origin)
    {
        List<string> failures = [];

        foreach (SelfTestCase test in pipeline.SelfTests)
        {
            string actual = pipeline.Normalize(test.Input);

            if (!string.Equals(actual, test.Expected, StringComparison.Ordinal))
            {
                failures.Add(
                    $"    '{test.Input}' produced '{actual}', expected '{test.Expected}'");
            }
        }

        if (failures.Count == 0)
        {
            return;
        }

        StringBuilder message = new();
        message.Append(origin).Append(" [").Append(pipeline.Tag).Append("]: ");
        message.Append(failures.Count).Append(failures.Count == 1 ? " self-test failed:" : " self-tests failed:");

        foreach (string failure in failures)
        {
            message.Append('\n').Append(failure);
        }

        throw new RuleSetException(message.ToString());
    }

    private static ICompiledOperation CompileOperation(
        RuleOperation operation,
        string tag,
        string origin,
        int index)
    {
        string where = $"{origin} [{tag}] pipeline[{index}]";

        if (string.IsNullOrWhiteSpace(operation.Op))
        {
            throw new RuleSetException($"{where}: 'op' must be set.");
        }

        return operation.Op switch
        {
            RuleOperations.NormalizeForm => CompileNormalizeForm(operation, where),
            RuleOperations.MapChars => CompileMapChars(operation, where),
            RuleOperations.MapSequences => CompileMapSequences(operation, where),
            RuleOperations.Lowercase => CompileLowercase(operation, where),
            RuleOperations.StripUnicodeCategory => CompileStripCategory(operation, where),
            RuleOperations.CollapseWhitespace => new CollapseWhitespaceOperation(),
            RuleOperations.DropTokens => CompileDropTokens(operation, where),
            _ => throw new RuleSetException(
                $"{where}: '{operation.Op}' is not a declared operation. "
                + $"The operation set is closed: {string.Join(", ", RuleOperations.All)}. "
                + "Adding a seventh is a code change made deliberately, never an escape hatch."),
        };
    }

    private static ICompiledOperation CompileDropTokens(RuleOperation operation, string where)
    {
        if (operation.Tokens is null || operation.Tokens.Count == 0)
        {
            throw new RuleSetException(
                $"{where}: 'dropTokens' requires a non-empty 'tokens' array. An empty one is a "
                + "no-op that reads like a configured stopword list.");
        }

        foreach (string token in operation.Tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new RuleSetException($"{where}: 'tokens' contains a blank entry.");
            }

            foreach (char c in token)
            {
                if (char.IsWhiteSpace(c))
                {
                    throw new RuleSetException(
                        $"{where}: 'tokens' contains '{token}', which has whitespace in it. "
                        + "Matching is on whole tokens, so a multi-word entry can never match.");
                }
            }
        }

        return new DropTokensOperation(operation.Tokens);
    }

    private static ICompiledOperation CompileNormalizeForm(RuleOperation operation, string where)
    {
        if (!TextRuntime.SupportsNormalization)
        {
            throw new RuleSetException(
                $"{where}: this runtime cannot perform Unicode normalization, so 'normalizeForm' "
                + "would silently do nothing. string.Normalize returns its input unchanged under "
                + "globalization-invariant mode, and IsNormalized agrees, so the no-op is not "
                + "observable at runtime. Fold the characters you need explicitly with 'mapChars' "
                + "instead — for example \"é\": \"e\" — which is what the shipped rule sets do.");
        }

        if (string.IsNullOrWhiteSpace(operation.Form))
        {
            throw new RuleSetException(
                $"{where}: 'normalizeForm' requires 'form' (FormC, FormD, FormKC or FormKD).");
        }

        NormalizationForm form = operation.Form switch
        {
            _ when Matches(operation.Form, "FormC") => NormalizationForm.FormC,
            _ when Matches(operation.Form, "FormD") => NormalizationForm.FormD,
            _ when Matches(operation.Form, "FormKC") => NormalizationForm.FormKC,
            _ when Matches(operation.Form, "FormKD") => NormalizationForm.FormKD,
            _ => throw new RuleSetException(
                $"{where}: 'form' is '{operation.Form}'; expected FormC, FormD, FormKC or FormKD."),
        };

        return new NormalizeFormOperation(form);
    }

    private static ICompiledOperation CompileMapChars(RuleOperation operation, string where)
    {
        if (operation.Map is null || operation.Map.Count == 0)
        {
            throw new RuleSetException($"{where}: 'mapChars' requires a non-empty 'map'.");
        }

        Dictionary<char, string> map = [];

        foreach (KeyValuePair<string, string> entry in operation.Map)
        {
            if (entry.Key.Length != 1)
            {
                throw new RuleSetException(
                    $"{where}: 'mapChars' key '{entry.Key}' is {entry.Key.Length} characters. "
                    + "Keys must be exactly one character; use 'mapSequences' for longer keys.");
            }

            map[entry.Key[0]] = entry.Value;
        }

        return new MapCharsOperation(map);
    }

    private static ICompiledOperation CompileMapSequences(RuleOperation operation, string where)
    {
        if (operation.Map is null || operation.Map.Count == 0)
        {
            throw new RuleSetException($"{where}: 'mapSequences' requires a non-empty 'map'.");
        }

        foreach (string key in operation.Map.Keys)
        {
            if (key.Length == 0)
            {
                throw new RuleSetException($"{where}: 'mapSequences' has an empty key.");
            }
        }

        return new MapSequencesOperation(operation.Map);
    }

    private static ICompiledOperation CompileLowercase(RuleOperation operation, string where)
    {
        if (string.IsNullOrWhiteSpace(operation.Mode))
        {
            throw new RuleSetException($"{where}: 'lowercase' requires 'mode' (ascii or invariant).");
        }

        if (Matches(operation.Mode, LowercaseOperation.AsciiMode))
        {
            return new LowercaseOperation(asciiOnly: true);
        }

        if (Matches(operation.Mode, LowercaseOperation.InvariantMode))
        {
            return new LowercaseOperation(asciiOnly: false);
        }

        throw new RuleSetException(
            $"{where}: 'mode' is '{operation.Mode}'; expected 'ascii' or 'invariant'.");
    }

    private static ICompiledOperation CompileStripCategory(RuleOperation operation, string where)
    {
        if (operation.Categories is null || operation.Categories.Count == 0)
        {
            throw new RuleSetException(
                $"{where}: 'stripUnicodeCategory' requires a non-empty 'categories'.");
        }

        List<UnicodeCategory> categories = [];

        foreach (string name in operation.Categories)
        {
            UnicodeCategory? parsed = ParseCategory(name);

            if (parsed is null)
            {
                throw new RuleSetException(
                    $"{where}: '{name}' is not a Unicode category. Valid names are the members of "
                    + "System.Globalization.UnicodeCategory, for example NonSpacingMark.");
            }

            if (parsed == UnicodeCategory.Format)
            {
                throw new RuleSetException(
                    $"{where}: stripping the 'Format' category is refused. It contains U+200C "
                    + "ZERO WIDTH NON-JOINER, which is meaningful in Persian, so removing the "
                    + "category wholesale corrupts text while appearing to work. Remove the "
                    + "specific characters with 'mapChars' instead.");
            }

            categories.Add(parsed.Value);
        }

        return new StripUnicodeCategoryOperation(categories);
    }

    private static UnicodeCategory? ParseCategory(string name)
    {
        foreach (UnicodeCategory category in Enum.GetValues<UnicodeCategory>())
        {
            if (Matches(name, category.ToString()))
            {
                return category;
            }
        }

        return null;
    }

    private static bool Matches(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
