namespace Neadocs.Engine.Infrastructure.Retrieval;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Features;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Storage;
using Neadocs.Engine.Infrastructure.Text;
using Npgsql;

public sealed class SearchService
{
    private readonly NpgsqlDataSourceFactory _connections;
    private readonly DocumentStore _store;
    private readonly LexicalSearch _lexical;
    private readonly VectorSearch _vector;
    private readonly ChunkDetailReader _details;
    private readonly DocumentEngineOptions _options;
    private readonly ILogger<SearchService> _logger;

    public SearchService(
        NpgsqlDataSourceFactory connections,
        DocumentStore store,
        LexicalSearch lexical,
        VectorSearch vector,
        ChunkDetailReader details,
        IOptions<DocumentEngineOptions> options,
        ILogger<SearchService> logger)
    {
        _connections = connections;
        _store = store;
        _lexical = lexical;
        _vector = vector;
        _details = details;
        _options = options.Value;
        _logger = logger;
    }

    public bool HasEmbeddingModel => _vector.Available;

    public async Task<SearchResponse?> SearchAsync(
        string tenant, string collectionKey, SearchRequest request, string mode, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        Guid? collectionId = await _store.ResolveCollectionAsync(connection, null, tenant, collectionKey, ct);

        if (collectionId is null)
        {
            return null;
        }

        int limit = Math.Clamp(request.Limit <= 0 ? 10 : request.Limit, 1, _options.MaxSearchLimit);

        string? filter = request.Filter is null
            || request.Filter.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? null
                : request.Filter.Value.GetRawText();

        bool wantsLexical = !string.Equals(mode, "vector", StringComparison.OrdinalIgnoreCase);
        bool wantsVector = !string.Equals(mode, "lexical", StringComparison.OrdinalIgnoreCase);
        bool degraded = false;

        List<LexicalHit> lexicalHits = wantsLexical
            ? await _lexical.SearchAsync(collectionId.Value, request.Locale, request.Query, limit, filter, ct)
            : [];

        List<RankedChunk> trigramHits = [];

        if (wantsLexical && lexicalHits.Count < limit)
        {
            trigramHits = await _lexical.TrigramSearchAsync(
                collectionId.Value, request.Locale, request.Query, limit, filter, ct);
        }

        List<RankedChunk> vectorHits = [];

        if (wantsVector)
        {
            if (!_vector.Available)
            {
                degraded = true;
            }
            else
            {
                try
                {
                    vectorHits = await _vector.SearchAsync(
                        collectionId.Value, request.Locale, _lexical.LocaleChain(request.Locale),
                        request.Query, limit, filter, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    degraded = true;
                    _logger.LogWarning(ex,
                        "Vector search failed; falling back to lexical for this request.");
                }
            }
        }

        string effectiveMode = (wantsLexical, wantsVector && !degraded) switch
        {
            (true, true) => "hybrid",
            (false, true) => "vector",
            _ => "lexical",
        };

        List<FusedChunk> lexicalSide = RrfFuser.Fuse(
            [.. lexicalHits.Select(h => new RankedChunk(h.ChunkId, h.Ordinal, h.Score))],
            trigramHits,
            _options.RrfK);

        List<FusedChunk> fused = RrfFuser.Fuse(
            [.. lexicalSide.Select(f => new RankedChunk(f.ChunkId, 0, f.Score))],
            vectorHits,
            _options.RrfK);

        Dictionary<Guid, LexicalHit> lexicalById = [];

        foreach (LexicalHit hit in lexicalHits)
        {
            lexicalById[hit.ChunkId] = hit;
        }

        List<Guid> missing = [.. fused
            .Select(f => f.ChunkId)
            .Where(id => !lexicalById.ContainsKey(id))
            .Take(limit * 2)];

        Dictionary<Guid, ChunkDetail> extra = await _details.LoadAsync(missing, ct);

        IReadOnlyList<string> chain = _lexical.LocaleChain(request.Locale);

        // Emphasis for hits the lexical pass never produced a headline for — everything the fuzzy
        // and vector passes contributed.
        HashSet<string> queryTerms = _lexical.QueryTerms(request.Locale, request.Query);
        CompiledPipeline requestPipeline = _lexical.PipelineFor(request.Locale);

        List<SearchHit> hits = [];

        foreach (FusedChunk chunk in Order(fused, lexicalById, extra, chain))
        {
            if (chunk.Score < request.MinScore)
            {
                continue;
            }

            SearchHit? hit = Materialise(
                chunk, lexicalById, extra, queryTerms, requestPipeline, request.Explain);

            if (hit is not null)
            {
                hits.Add(hit);
            }

            if (hits.Count >= limit)
            {
                break;
            }
        }

        return new SearchResponse
        {
            Mode = effectiveMode,
            Degraded = degraded,
            Hits = hits,
        };
    }

    /// <summary>
    /// Final ordering: the requested locale first, then relevance within each locale.
    /// </summary>
    /// <remarks>
    /// <b>Locale outranks score, and that is the change.</b> This used to sort by score and use the
    /// locale only to break exact ties — which almost never happen, because RRF scores differ by
    /// construction (1/61, 1/62, …). The locale preference was therefore expressed in the code and
    /// absent from the results: a Turkish reader asking a Turkish question received four English
    /// articles above the Turkish one, none of which had matched anything in particular.
    /// <para>
    /// A fallback locale exists so that a reader finds <i>something</i> when their own language has
    /// no article on the subject. It is not a competitor to their own language, and letting it win
    /// on score turns a safety net into a hazard — the reader cannot tell "there is no Turkish
    /// article" from "the Turkish article was ranked fifth".
    /// </para>
    /// <para>
    /// Ordered by position in the chain rather than by a primary/other flag, so a three-locale
    /// chain degrades in the order the operator declared instead of collapsing into two tiers.
    /// </para>
    /// </remarks>
    private static IEnumerable<FusedChunk> Order(
        List<FusedChunk> fused,
        Dictionary<Guid, LexicalHit> lexical,
        Dictionary<Guid, ChunkDetail> extra,
        IReadOnlyList<string> chain)
    {
        if (chain.Count == 0)
        {
            return fused;
        }

        return fused
            .OrderBy(f => LocaleRank(LocaleOf(f.ChunkId, lexical, extra), chain))
            .ThenByDescending(f => f.Score)
            .ThenBy(f => f.ChunkId);
    }

    /// <summary>
    /// Where a document's locale sits in the requested chain. Anything not in the chain sorts after
    /// everything that is — it can only have arrived through a filter-less search.
    /// </summary>
    private static int LocaleRank(string locale, IReadOnlyList<string> chain)
    {
        for (int i = 0; i < chain.Count; i++)
        {
            if (string.Equals(chain[i], locale, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return chain.Count;
    }

    private static string LocaleOf(
        Guid chunkId, Dictionary<Guid, LexicalHit> lexical, Dictionary<Guid, ChunkDetail> extra) =>
        lexical.TryGetValue(chunkId, out LexicalHit? hit)
            ? hit.Locale
            : extra.TryGetValue(chunkId, out ChunkDetail? detail) ? detail.Locale : string.Empty;

    private static SearchHit? Materialise(
        FusedChunk chunk,
        Dictionary<Guid, LexicalHit> lexical,
        Dictionary<Guid, ChunkDetail> extra,
        IReadOnlyCollection<string> queryTerms,
        CompiledPipeline pipeline,
        bool explain)
    {
        SearchExplain? explanation = explain
            ? new SearchExplain
            {
                LexicalRank = chunk.LexicalRank,
                VectorRank = chunk.VectorRank,
                LexicalScore = chunk.LexicalScore,
                VectorScore = chunk.VectorScore,
            }
            : null;

        if (lexical.TryGetValue(chunk.ChunkId, out LexicalHit? hit))
        {
            return new SearchHit
            {
                ChunkId = hit.ChunkId,
                DocumentId = hit.DocumentId,
                ExternalKey = hit.ExternalKey,
                Locale = hit.Locale,
                Title = hit.Title,
                HeadingPath = LexicalSearch.HeadingPathFrom(hit.HeadingPathJson),
                Score = chunk.Score,
                Ordinal = hit.Ordinal,
                Snippet = hit.Snippet,
                Highlights = LexicalSearch.HighlightsFrom(hit.Snippet, hit.Content),
                Metadata = JsonDocument.Parse(hit.MetadataJson).RootElement.Clone(),
                Explain = explanation,
            };
        }

        if (!extra.TryGetValue(chunk.ChunkId, out ChunkDetail? detail))
        {
            return null;
        }

        string snippet = LexicalSearch.ExcerptAround(detail.Content, queryTerms, pipeline, ExcerptLength);

        return new SearchHit
        {
            ChunkId = detail.ChunkId,
            DocumentId = detail.DocumentId,
            ExternalKey = detail.ExternalKey,
            Locale = detail.Locale,
            Title = detail.Title,
            HeadingPath = LexicalSearch.HeadingPathFrom(detail.HeadingPathJson),
            Score = chunk.Score,
            Ordinal = detail.Ordinal,
            Snippet = snippet,
            // Derived from the emphasis just applied, so the marked text and the offsets that
            // describe it cannot disagree. This list used to be unconditionally empty.
            Highlights = LexicalSearch.HighlightsFrom(snippet, detail.Content),
            Metadata = JsonDocument.Parse(detail.MetadataJson).RootElement.Clone(),
            Explain = explanation,
        };
    }

    /// <summary>Roughly what `ts_headline` produces for the lexical path, so the two look alike.</summary>
    private const int ExcerptLength = 240;
}
