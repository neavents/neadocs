namespace Neadocs.Engine.Infrastructure.Retrieval;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Diagnostics;
using Neadocs.Engine.Infrastructure.Providers;
using Neadocs.Engine.Infrastructure.Storage;
using Neadocs.Engine.Infrastructure.Text;
using Npgsql;
using NpgsqlTypes;

public sealed class VectorSearch
{
    private readonly NpgsqlDataSourceFactory _connections;
    private readonly SchemaTables _tables;
    private readonly EmbeddingChain _chain;
    private readonly EmbeddingStore _embeddings;
    private readonly EmbeddingModelRegistry _models;
    private readonly NormalizerRegistry _normalizers;
    private readonly VectorTypeInfo _vectorType;
    private readonly DocumentEngineOptions _options;

    public VectorSearch(
        NpgsqlDataSourceFactory connections,
        SchemaTables tables,
        EmbeddingChain chain,
        EmbeddingStore embeddings,
        EmbeddingModelRegistry models,
        NormalizerRegistry normalizers,
        VectorTypeInfo vectorType,
        IOptions<DocumentEngineOptions> options)
    {
        _connections = connections;
        _tables = tables;
        _chain = chain;
        _embeddings = embeddings;
        _models = models;
        _normalizers = normalizers;
        _vectorType = vectorType;
        _options = options.Value;
    }

    public bool Available => _chain.HasProvider && _models.Primary is not null && _vectorType.Available;

    public async Task<List<RankedChunk>> SearchAsync(
        Guid collectionId,
        string? locale,
        IReadOnlyList<string> localeChain,
        string rawQuery,
        int limit,
        string? metadataFilterJson,
        CancellationToken ct)
    {
        EmbeddingModelDescriptor? model = _models.Primary;

        if (model is null || !_chain.HasProvider)
        {
            return [];
        }

        using Activity? activity = NeadocsActivitySources.Search.StartActivity("search.vector");
        activity?.SetTag(NeadocsTags.Model, model.Slug);

        string folded = _normalizers.Normalize(locale, rawQuery);

        // Through the cache, not straight at the provider. This is the read path: an uncached call
        // here is a network round trip on every search, which took the mean from single-digit
        // milliseconds to several hundred. Help-centre questions repeat heavily across readers, so
        // the second person to ask anything pays nothing.
        float[]? embedded = await _embeddings.EmbedQueryAsync(model.Slug, folded, ct);

        if (embedded is null)
        {
            return [];
        }

        string queryVector = EmbeddingStore.Literal(embedded);

        // Deliberately a tighter budget than the lexical pass gets, because the two kinds of
        // candidate are not comparable. A lexical candidate has *matched* something — it satisfied
        // the tsquery — so a wide net costs little. A vector candidate is merely the next nearest,
        // and on a small corpus a wide net returns most of the corpus regardless of relevance.
        // Fusing 50 of those against a handful of real lexical matches drowns them: short queries
        // that lexical answers confidently lost their answer entirely.
        int candidates = Math.Max(limit, _options.MinCandidates / _options.CandidateMultiplier);

        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        await using (NpgsqlCommand tune = _connections.CreateCommand(
            connection, $"SET hnsw.ef_search = {_options.HnswEfSearch}"))
        {
            await tune.ExecuteNonQueryAsync(ct);
        }

        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            SELECT c.id, c.ordinal, 1 - (e.embedding OPERATOR({_vectorType.Schema}.<=>) @query::{_vectorType.TypeName}) AS score
            FROM {_tables.ChunkEmbeddings(model.Slug)} e
            JOIN {_tables.Chunks} c ON c.id = e.chunk_id
            JOIN {_tables.Documents} d
              ON d.id = c.document_id AND d.current_revision = c.revision
            WHERE d.collection_id = @collection
              AND d.deleted_at IS NULL
              AND (@useLocale = FALSE OR d.locale = ANY(@chain))
              AND (@filter IS NULL OR d.metadata @> @filter::jsonb)
            ORDER BY e.embedding OPERATOR({_vectorType.Schema}.<=>) @query::{_vectorType.TypeName}
            LIMIT @limit
            """);

        command.Parameters.AddWithValue("query", NpgsqlDbType.Text, queryVector);
        command.Parameters.AddWithValue("collection", collectionId);
        command.Parameters.AddWithValue("useLocale", localeChain.Count > 0);
        command.Parameters.AddWithValue("chain",
            localeChain.Count > 0 ? (object)localeChain : Array.Empty<string>());
        command.Parameters.AddWithValue("filter", NpgsqlDbType.Text, (object?)metadataFilterJson ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", candidates);

        List<RankedChunk> hits = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            double similarity = reader.GetDouble(2);

            // Filtered here rather than in the WHERE clause: a predicate on the distance expression
            // would stop the HNSW index being used and turn every search into a sequential scan.
            // The candidate list is bounded and already ordered, so this costs nothing.
            if (similarity < _options.VectorMinSimilarity)
            {
                continue;
            }

            hits.Add(new RankedChunk(reader.GetGuid(0), reader.GetInt32(1), similarity));
        }

        activity?.SetTag(NeadocsTags.HitCount, hits.Count);

        return hits;
    }
}
