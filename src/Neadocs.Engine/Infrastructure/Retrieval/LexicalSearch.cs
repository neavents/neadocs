namespace Neadocs.Engine.Infrastructure.Retrieval;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Features;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Diagnostics;
using Neadocs.Engine.Infrastructure.Storage;
using Neadocs.Engine.Infrastructure.Text;
using Npgsql;
using NpgsqlTypes;

public sealed record LexicalHit(
    Guid ChunkId,
    Guid DocumentId,
    string ExternalKey,
    string Locale,
    string Title,
    string HeadingPathJson,
    string MetadataJson,
    int Ordinal,
    double Score,
    string Snippet,
    string Content);

public sealed class LexicalSearch
{
    private readonly NpgsqlDataSourceFactory _connections;
    private readonly SchemaTables _tables;
    private readonly NormalizerRegistry _normalizers;
    private readonly SynonymExpander _synonyms;
    private readonly VectorTypeInfo _extensions;
    private readonly DocumentEngineOptions _options;

    public LexicalSearch(
        NpgsqlDataSourceFactory connections,
        SchemaTables tables,
        NormalizerRegistry normalizers,
        SynonymExpander synonyms,
        VectorTypeInfo extensions,
        IOptions<DocumentEngineOptions> options)
    {
        _connections = connections;
        _tables = tables;
        _normalizers = normalizers;
        _synonyms = synonyms;
        _extensions = extensions;
        _options = options.Value;
    }

    /// <summary>
    /// The normalized query, plus any synonym expansions, as separate alternatives.
    /// </summary>
    /// <remarks>
    /// Kept as a list rather than joined immediately because the prefix branch has to be built
    /// per alternative. Truncating the joined string would truncate the literal <c>OR</c> between
    /// them as well, collapsing "this phrase or that phrase" into one long conjunction — a query
    /// that silently matches almost nothing.
    /// </remarks>
    private IReadOnlyList<string> QueryAlternatives(string? locale, string rawQuery)
    {
        string folded = _normalizers.Normalize(locale, rawQuery);

        if (folded.Length == 0)
        {
            return [];
        }

        List<string> alternatives = [folded];
        alternatives.AddRange(_synonyms.Expand(locale, folded));

        return alternatives;
    }

    public string BuildQueryText(string? locale, string rawQuery) =>
        string.Join(" OR ", QueryAlternatives(locale, rawQuery));

    /// <summary>
    /// The truncated companion query, matching the weight-D lexemes the indexer emits.
    /// </summary>
    /// <remarks>
    /// Empty for any locale whose rule set sets no <c>stemPrefixLength</c>, which is every locale
    /// that is not agglutinative — and for those the search behaves exactly as it did before.
    /// </remarks>
    public string BuildPrefixQueryText(string? locale, string rawQuery)
    {
        CompiledPipeline pipeline = _normalizers.Resolve(locale);

        if (!pipeline.EmitsPrefixes)
        {
            return string.Empty;
        }

        List<string> branches = [];

        foreach (string alternative in QueryAlternatives(locale, rawQuery))
        {
            string prefixes = pipeline.Prefixes(alternative);

            if (prefixes.Length > 0)
            {
                branches.Add(prefixes);
            }
        }

        return string.Join(" OR ", branches);
    }

    /// <summary>
    /// The locales a query may match, paired with the Postgres configuration each was indexed with.
    /// </summary>
    /// <remarks>
    /// A tsquery has to be parsed with the same configuration as the tsvector it is matched
    /// against, or the stemming disagrees and nothing matches: a Turkish document holding the
    /// lexeme <c>sifre</c> is invisible to a query parsed as <c>simple</c>, which leaves it as
    /// <c>sifremi</c>. So the query is built once per candidate locale and joined on the document's
    /// own locale.
    /// </remarks>
    private (string[] Locales, string[] Configs) SearchConfigs(IReadOnlyList<string> chain)
    {
        // With a locale chain, only those locales can match. Without one the caller asked to search
        // everything, so every declared locale needs its configuration — anything not declared
        // falls through to the default query.
        IReadOnlyList<string> candidates = chain.Count > 0 ? chain : _options.Text.Locales;

        string[] locales = new string[candidates.Count];
        string[] configs = new string[candidates.Count];

        for (int i = 0; i < candidates.Count; i++)
        {
            locales[i] = LocaleTag.Normalize(candidates[i]);
            configs[i] = _normalizers.Resolve(candidates[i]).SearchConfig;
        }

        return (locales, configs);
    }

    /// <summary>The compiled rule set a request's locale resolves to. Exposed for snippet emphasis.</summary>
    public CompiledPipeline PipelineFor(string? locale) => _normalizers.Resolve(locale);

    public IReadOnlyList<string> LocaleChain(string? locale)
    {
        IReadOnlyList<string> chain = _synonyms.LocaleChain(locale);

        return chain;
    }

    public int CandidateLimit(int limit) =>
        Math.Max(_options.MinCandidates, limit * _options.CandidateMultiplier);

    public async Task<List<LexicalHit>> SearchAsync(
        Guid collectionId,
        string? locale,
        string rawQuery,
        int limit,
        string? metadataFilterJson,
        CancellationToken ct)
    {
        using Activity? activity = NeadocsActivitySources.Search.StartActivity("search.lexical");
        activity?.SetTag(NeadocsTags.Locale, locale);
        activity?.SetTag(NeadocsTags.QueryLength, rawQuery.Length);

        string queryText = BuildQueryText(locale, rawQuery);

        if (queryText.Length == 0)
        {
            return [];
        }

        string prefixQuery = BuildPrefixQueryText(locale, rawQuery);
        CompiledPipeline requestPipeline = _normalizers.Resolve(locale);
        HashSet<string> queryTerms = QueryTerms(locale, rawQuery);
        IReadOnlyList<string> chain = LocaleChain(locale);
        (string[] configLocales, string[] configNames) = SearchConfigs(chain);

        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        // `def` covers documents in a locale nothing declares a configuration for. It is a LEFT
        // JOIN plus COALESCE rather than an inner join precisely so those documents stay findable —
        // an undeclared locale should search less well, never vanish.
        //
        // The CASE is not decoration: `websearch_to_tsquery(cfg, '')` returns an empty tsquery and
        // emits a NOTICE, so concatenating unconditionally would log one per locale per search for
        // every language that wants no prefixes, which is most of them.
        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            WITH def AS (
                SELECT CASE WHEN @prefixQuery = ''
                            THEN websearch_to_tsquery('simple', @query)
                            ELSE websearch_to_tsquery('simple', @query)
                                 || websearch_to_tsquery('simple', @prefixQuery)
                       END AS query
            ),
            q AS (
                SELECT t.loc,
                       CASE WHEN @prefixQuery = ''
                            THEN websearch_to_tsquery(t.cfg::regconfig, @query)
                            ELSE websearch_to_tsquery(t.cfg::regconfig, @query)
                                 || websearch_to_tsquery('simple', @prefixQuery)
                       END AS query
                FROM unnest(@configLocales, @configNames) AS t(loc, cfg)
            )
            SELECT c.id, c.document_id, d.external_key, d.locale, d.title,
                   c.heading_path::text, d.metadata::text, c.ordinal,
                   ts_rank_cd(c.tsv_folded, COALESCE(q.query, def.query)) AS score,
                   ts_headline('simple', c.content, COALESCE(q.query, def.query),
                       'StartSel=<em>,StopSel=</em>,MaxFragments=2,MinWords=8,MaxWords=30') AS snippet,
                   c.content
            FROM {_tables.Chunks} c
            JOIN {_tables.Documents} d
              ON d.id = c.document_id AND d.current_revision = c.revision
            CROSS JOIN def
            LEFT JOIN q ON q.loc = d.locale
            WHERE d.collection_id = @collection
              AND d.deleted_at IS NULL
              AND (@useLocale = FALSE OR d.locale = ANY(@chain))
              AND (@filter IS NULL OR d.metadata @> @filter::jsonb)
              AND c.tsv_folded @@ COALESCE(q.query, def.query)
            -- Score first here, unlike the trigram pass. Lexical scores discriminate, so ordering
            -- by locale would spend the candidate budget on weak primary-locale matches and leave
            -- a strong fallback out of the pool entirely. The final locale preference is applied
            -- once, after fusion, in SearchService.Order.
            ORDER BY score DESC,
                     (CASE WHEN @useLocale AND d.locale = @primary THEN 0 ELSE 1 END),
                     c.ordinal ASC,
                     c.id ASC
            LIMIT @limit
            """);

        command.Parameters.AddWithValue("query", queryText);
        command.Parameters.AddWithValue("prefixQuery", prefixQuery);
        command.Parameters.AddWithValue("configLocales", configLocales);
        command.Parameters.AddWithValue("configNames", configNames);
        command.Parameters.AddWithValue("collection", collectionId);
        command.Parameters.AddWithValue("useLocale", chain.Count > 0);
        command.Parameters.AddWithValue("chain", chain.Count > 0 ? (object)chain : Array.Empty<string>());
        command.Parameters.AddWithValue("primary", chain.Count > 0 ? chain[0] : string.Empty);
        command.Parameters.AddWithValue("filter", NpgsqlDbType.Text, (object?)metadataFilterJson ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", CandidateLimit(limit));

        List<LexicalHit> hits = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            hits.Add(new LexicalHit(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetFloat(8),
                // Emphasis is applied here rather than downstream so that `HighlightsFrom` — which
                // reads the markers back out to produce offsets — sees the same snippet a reader
                // will. Fixing one without the other would leave a hit whose highlighted text and
                // highlight offsets disagreed.
                EmphasiseFolded(reader.GetString(9), queryTerms, requestPipeline),
                reader.GetString(10)));
        }

        activity?.SetTag(NeadocsTags.HitCount, hits.Count);

        return hits;
    }


    /// <summary>
    /// The fuzzy fallback, run only when the lexical pass returns fewer candidates than asked for.
    /// </summary>
    /// <remarks>
    /// <b>Locale sorts ahead of score here, unlike the lexical pass, because these scores tie
    /// constantly.</b> <c>word_similarity</c> compares a term against the best-matching word in the
    /// content, and a short word in one language routinely scores identically to a longer inflected
    /// word in another — 0.571 against both, in the case that prompted this. Ordering by score
    /// alone therefore left <c>c.id</c> as the real tiebreak, which decided by row order which
    /// language a reader saw first, and could exhaust the candidate budget on the fallback locale
    /// before a single primary-locale chunk was considered.
    /// </remarks>
    public async Task<List<RankedChunk>> TrigramSearchAsync(
        Guid collectionId,
        string? locale,
        string rawQuery,
        int limit,
        string? metadataFilterJson,
        CancellationToken ct)
    {
        string folded = _normalizers.Normalize(locale, rawQuery);

        List<string> terms = [];

        foreach (string term in folded.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (term.Length >= 4)
            {
                terms.Add(term);
            }
        }

        foreach (string expansion in _synonyms.Expand(locale, folded))
        {
            foreach (string term in expansion.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (term.Length >= 4 && !terms.Exists(t => string.Equals(t, term, StringComparison.Ordinal)))
                {
                    terms.Add(term);
                }
            }
        }

        if (terms.Count == 0)
        {
            return [];
        }

        using Activity? activity = NeadocsActivitySources.Search.StartActivity("search.trigram");

        IReadOnlyList<string> chain = LocaleChain(locale);

        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        await using (NpgsqlCommand tune = _connections.CreateCommand(
            connection,
            $"SET pg_trgm.word_similarity_threshold = {_options.Text.TrigramThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
        {
            await tune.ExecuteNonQueryAsync(ct);
        }

        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            SELECT c.id, c.ordinal,
                   max({_extensions.TrigramSchema}.word_similarity(t.term, c.content)) AS score
            FROM unnest(@terms) AS t(term)
            JOIN {_tables.Chunks} c ON t.term OPERATOR({_extensions.TrigramSchema}.<%) c.content
            JOIN {_tables.Documents} d
              ON d.id = c.document_id AND d.current_revision = c.revision
            WHERE d.collection_id = @collection
              AND d.deleted_at IS NULL
              AND (@useLocale = FALSE OR d.locale = ANY(@chain))
              AND (@filter IS NULL OR d.metadata @> @filter::jsonb)
            GROUP BY c.id, c.ordinal, d.locale
            -- See TrigramSearchAsync's summary for why locale sorts ahead of score here.
            ORDER BY (CASE WHEN @useLocale AND d.locale = @primary THEN 0 ELSE 1 END),
                     score DESC,
                     c.ordinal ASC,
                     c.id ASC
            LIMIT @limit
            """);

        command.Parameters.AddWithValue("terms", terms.ToArray());
        command.Parameters.AddWithValue("collection", collectionId);
        command.Parameters.AddWithValue("useLocale", chain.Count > 0);
        command.Parameters.AddWithValue("chain", chain.Count > 0 ? (object)chain : Array.Empty<string>());
        command.Parameters.AddWithValue("primary", chain.Count > 0 ? chain[0] : string.Empty);
        command.Parameters.AddWithValue("filter", NpgsqlDbType.Text, (object?)metadataFilterJson ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", CandidateLimit(limit));

        List<RankedChunk> hits = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            hits.Add(new RankedChunk(reader.GetGuid(0), reader.GetInt32(1), reader.GetFloat(2)));
        }

        activity?.SetTag(NeadocsTags.HitCount, hits.Count);

        return hits;
    }

    /// <summary>
    /// Marks the words a reader would recognise as their query, when the database could not.
    /// </summary>
    /// <remarks>
    /// <b>`ts_headline` cannot emphasise text that was folded before it was indexed.</b> It is given
    /// the raw content and a query built from the folded, stemmed text, so the two only agree for
    /// languages whose folded form equals their raw form. English is such a language and got
    /// emphasis; Turkish is not, and every Turkish result came back with no <c>&lt;em&gt;</c> and
    /// no highlights at all — the reader losing the one cue that says why this result is here.
    /// <para>
    /// Matching happens on the folded axis, which is the only axis on which the query and the
    /// content are comparable. A word matches when its folded form equals a folded query term, or —
    /// where the locale emits them — when the two share a truncation, which is what lets an
    /// inflected form in the text light up for an inflected form in the query.
    /// </para>
    /// <para>
    /// The original characters are never rewritten: only the markers are inserted, so the reader
    /// sees their own language with its diacritics intact. And it defers entirely when the database
    /// already produced emphasis, so a locale that was working keeps exactly the behaviour it had.
    /// </para>
    /// </remarks>
    public static string EmphasiseFolded(
        string snippet, IReadOnlyCollection<string> queryTerms, CompiledPipeline pipeline)
    {
        if (snippet.Length == 0 || queryTerms.Count == 0
            || snippet.Contains("<em>", StringComparison.OrdinalIgnoreCase))
        {
            return snippet;
        }

        StringBuilder builder = new(snippet.Length + 32);
        int index = 0;

        while (index < snippet.Length)
        {
            if (!char.IsLetterOrDigit(snippet[index]))
            {
                builder.Append(snippet[index]);
                index++;
                continue;
            }

            int start = index;

            while (index < snippet.Length && char.IsLetterOrDigit(snippet[index]))
            {
                index++;
            }

            string word = snippet[start..index];

            if (Matches(word, queryTerms, pipeline))
            {
                builder.Append("<em>").Append(word).Append("</em>");
            }
            else
            {
                builder.Append(word);
            }
        }

        return builder.ToString();
    }

    private static bool Matches(
        string word, IReadOnlyCollection<string> queryTerms, CompiledPipeline pipeline)
    {
        string folded = pipeline.Normalize(word);

        // Empty means the pipeline dropped it — a function word, which is exactly what should not
        // be lit up. It carries no information about why this result matched.
        if (folded.Length == 0)
        {
            return false;
        }

        foreach (string term in queryTerms)
        {
            if (string.Equals(folded, term, StringComparison.Ordinal))
            {
                return true;
            }

            if (pipeline.EmitsPrefixes
                && folded.Length >= pipeline.StemPrefixLength
                && term.Length >= pipeline.StemPrefixLength
                && folded.AsSpan(0, pipeline.StemPrefixLength)
                    .SequenceEqual(term.AsSpan(0, pipeline.StemPrefixLength)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A window of text centred on the first word that matched, with that word emphasised.
    /// </summary>
    /// <remarks>
    /// <b>For hits no lexical pass produced a headline for.</b> A chunk found only by the fuzzy pass
    /// arrived with the first 240 characters of its content as a preview and an empty highlight
    /// list — so the reader was shown the opening of a section that may not mention their query
    /// anywhere in it, and given no indication of why it was returned. A preview whose relevance is
    /// invisible is close to no preview at all.
    /// <para>
    /// Falls back to the opening of the chunk when nothing matches, which is the honest answer: the
    /// fuzzy pass matched on character similarity, and there may genuinely be no whole word to
    /// point at.
    /// </para>
    /// </remarks>
    public static string ExcerptAround(
        string content,
        IReadOnlyCollection<string> queryTerms,
        CompiledPipeline pipeline,
        int maxLength)
    {
        if (content.Length == 0)
        {
            return content;
        }

        int match = FirstMatchOffset(content, queryTerms, pipeline);

        // Roughly a third of the window ahead of the match, so the reader sees the sentence it sits
        // in rather than the match hanging off the left edge.
        int lead = maxLength / 3;
        int start = match < 0 ? 0 : Math.Max(0, match - lead);

        start = SnapToWordStart(content, start);

        int end = Math.Min(content.Length, start + maxLength);
        end = SnapToWordEnd(content, end);

        string window = content[start..end];
        string emphasised = EmphasiseFolded(window, queryTerms, pipeline);

        return (start > 0 ? "…" : string.Empty) + emphasised + (end < content.Length ? "…" : string.Empty);
    }

    private static int FirstMatchOffset(
        string text, IReadOnlyCollection<string> queryTerms, CompiledPipeline pipeline)
    {
        if (queryTerms.Count == 0)
        {
            return -1;
        }

        int index = 0;

        while (index < text.Length)
        {
            if (!char.IsLetterOrDigit(text[index]))
            {
                index++;
                continue;
            }

            int start = index;

            while (index < text.Length && char.IsLetterOrDigit(text[index]))
            {
                index++;
            }

            if (Matches(text[start..index], queryTerms, pipeline))
            {
                return start;
            }
        }

        return -1;
    }

    /// <summary>
    /// How far the window edges may travel to reach a word boundary.
    /// </summary>
    /// <remarks>
    /// Unbounded snapping is defeated by a single long token — a URL, a base64 blob, minified
    /// output pasted into a document. The edge walks the whole token, and the window ends up
    /// somewhere the match is not, which is the one outcome the centring exists to prevent. Past
    /// this distance, cutting mid-word is the lesser fault: it looks untidy, where the alternative
    /// silently shows the wrong text.
    /// </remarks>
    private const int MaxSnapDistance = 32;

    /// <summary>Walks back to the start of the word an offset lands inside, so a preview never opens mid-word.</summary>
    private static int SnapToWordStart(string text, int offset)
    {
        if (offset <= 0)
        {
            return 0;
        }

        int at = offset;
        int limit = Math.Max(0, offset - MaxSnapDistance);

        while (at > limit && char.IsLetterOrDigit(text[at - 1]))
        {
            at--;
        }

        return at;
    }

    private static int SnapToWordEnd(string text, int offset)
    {
        if (offset >= text.Length)
        {
            return text.Length;
        }

        int at = offset;
        int limit = Math.Min(text.Length, offset + MaxSnapDistance);

        while (at < limit && char.IsLetterOrDigit(text[at]))
        {
            at++;
        }

        return at;
    }

    /// <summary>The distinct folded terms of a query, with the alternation structure flattened away.</summary>
    public HashSet<string> QueryTerms(string? locale, string rawQuery)
    {
        HashSet<string> terms = new(StringComparer.Ordinal);

        foreach (string alternative in QueryAlternatives(locale, rawQuery))
        {
            foreach (string term in alternative.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                terms.Add(term);
            }
        }

        return terms;
    }

    public static List<Highlight> HighlightsFrom(string snippet, string content)
    {
        List<Highlight> highlights = [];
        int cursor = 0;

        while (true)
        {
            int open = snippet.IndexOf("<em>", cursor, StringComparison.Ordinal);

            if (open < 0)
            {
                break;
            }

            int close = snippet.IndexOf("</em>", open, StringComparison.Ordinal);

            if (close < 0)
            {
                break;
            }

            string term = snippet[(open + 4)..close];
            cursor = close + 5;

            if (term.Length == 0)
            {
                continue;
            }

            int at = content.IndexOf(term, StringComparison.Ordinal);

            if (at >= 0 && !highlights.Exists(h => h.Start == at))
            {
                highlights.Add(new Highlight { Start = at, Length = term.Length });
            }
        }

        return highlights;
    }

    public static List<string> HeadingPathFrom(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            List<string> segments = [];

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                segments.Add(element.GetString() ?? string.Empty);
            }

            return segments;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
