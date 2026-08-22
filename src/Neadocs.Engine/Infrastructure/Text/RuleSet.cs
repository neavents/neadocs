namespace Neadocs.Engine.Infrastructure.Text;

using System.Collections.Generic;
using System.Text.Json.Serialization;

public sealed class RuleSet
{
    public string Tag { get; set; } = string.Empty;

    public string? Description { get; set; }

    public List<RuleOperation> Pipeline { get; set; } = [];

    public List<SelfTestCase> SelfTest { get; set; } = [];

    /// <summary>
    /// The Postgres text-search configuration this locale's chunks are indexed with — a
    /// <c>regconfig</c> name such as <c>turkish</c>. Null or empty means <c>simple</c>, which
    /// applies no stemming at all.
    /// </summary>
    /// <remarks>
    /// <b>This is per locale because there is no single right answer.</b> One shared configuration
    /// forces a choice between stemming nothing (<c>simple</c>) and stemming everything with one
    /// language's rules, which is worse — English rules applied to Turkish do not merely fail, they
    /// conflate unrelated words.
    /// <para>
    /// It participates in <see cref="PipelineHash"/>, so changing it marks every chunk of that
    /// locale stale and a reindex rebuilds them. Without that, the column would hold a mixture of
    /// two indexing schemes and search would be quietly wrong for whichever half was older.
    /// </para>
    /// </remarks>
    public string? SearchConfig { get; set; }

    /// <summary>
    /// Emit a truncated copy of every token this long or longer, indexed at tsvector weight
    /// <c>D</c>. Zero or absent disables it.
    /// </summary>
    /// <remarks>
    /// <b>For agglutinative languages, where a stemmer is not enough.</b> Turkish suffixes stack —
    /// <c>menü</c> → <c>menüyü</c> → <c>menümü</c> → <c>menülerimiz</c> — and the snowball stemmer
    /// reduces some of those to the same root and others not at all: <c>menülerimiz</c> becomes
    /// <c>menü</c> while <c>menümü</c> becomes <c>men</c>. Since Turkish only ever suffixes, the
    /// root is always a <i>prefix</i> of the surface form, so a fixed-length truncation is a
    /// reliable meeting point where morphology is not.
    /// <para>
    /// Weight <c>D</c> is what keeps this from flooding results: <c>ts_rank_cd</c> weights it at
    /// 0.1 against 1.0 for the full token, so an exact match still outranks a truncated one. The
    /// truncation buys recall, not precision.
    /// </para>
    /// </remarks>
    public int StemPrefixLength { get; set; }
}

public sealed class RuleOperation
{
    public string Op { get; set; } = string.Empty;

    public string? Form { get; set; }

    public Dictionary<string, string>? Map { get; set; }

    public string? Mode { get; set; }

    public List<string>? Categories { get; set; }

    /// <summary>Whole tokens <c>dropTokens</c> removes. Compared verbatim, after every earlier step.</summary>
    public List<string>? Tokens { get; set; }
}

public sealed class SelfTestCase
{
    [JsonPropertyName("in")]
    public string Input { get; set; } = string.Empty;

    [JsonPropertyName("out")]
    public string Expected { get; set; } = string.Empty;
}

public static class RuleOperations
{
    public const string NormalizeForm = "normalizeForm";
    public const string MapChars = "mapChars";
    public const string MapSequences = "mapSequences";
    public const string Lowercase = "lowercase";
    public const string StripUnicodeCategory = "stripUnicodeCategory";
    public const string CollapseWhitespace = "collapseWhitespace";

    /// <summary>
    /// Removes whole tokens — the function words a query should not be required to match.
    /// </summary>
    /// <remarks>
    /// <b>This exists because folding defeats a dictionary's own stopword list.</b> Postgres ships
    /// stopwords written in each language's real orthography, and a pipeline that folds text to
    /// ASCII before the dictionary sees it hands over words the list no longer recognises: the
    /// Turkish list holds <c>nasıl</c>, the folded text says <c>nasil</c>, and the two never meet.
    /// Since <c>websearch_to_tsquery</c> joins terms with AND, every surviving function word became
    /// a term the document was <i>required</i> to contain — so a natural-language question found
    /// only documents that happened to phrase themselves the same way.
    /// <para>
    /// The right list for a folded pipeline is the dictionary's own list, folded by the same rules.
    /// Inventing one invites a maintained-by-nobody file that slowly stops matching the folding
    /// above it.
    /// </para>
    /// </remarks>
    public const string DropTokens = "dropTokens";

    public static readonly string[] All =
    [
        NormalizeForm,
        MapChars,
        MapSequences,
        Lowercase,
        StripUnicodeCategory,
        CollapseWhitespace,
        DropTokens,
    ];

    public const string FallbackTag = "*";

    public const int MinimumSelfTests = 3;

    /// <summary>The configuration used when a rule set names none, and for the weight-D prefixes.</summary>
    public const string DefaultSearchConfig = "simple";

    /// <summary>
    /// Bounds on <see cref="RuleSet.StemPrefixLength"/>. Below three a truncation is shared by most
    /// of the vocabulary and stops discriminating; above eight it no longer reaches past the
    /// suffixes it exists to cut through.
    /// </summary>
    public const int MinStemPrefixLength = 3;

    public const int MaxStemPrefixLength = 8;
}
