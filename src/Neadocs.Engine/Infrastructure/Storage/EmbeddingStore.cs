namespace Neadocs.Engine.Infrastructure.Storage;

using Neadocs.Engine.Infrastructure.Text;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Neadocs.Engine.Infrastructure.Diagnostics;
using Neadocs.Engine.Infrastructure.Providers;
using Npgsql;
using NpgsqlTypes;

/// <summary>
/// A chunk awaiting a vector, with the locale pipeline its text must be normalised by.
/// </summary>
/// <remarks>
/// The tag rather than the normalised text, so that normalising happens in exactly one place —
/// <see cref="EmbeddingStore"/> — and a new caller cannot introduce a path that embeds something
/// else. That is what went wrong: the query side normalised and the indexing side did not.
/// </remarks>
public sealed record PendingEmbedding(Guid ChunkId, string Text, string NormalizerTag);

/// <summary>
/// The cache key for a search query's embedding.
/// </summary>
/// <remarks>
/// Prefixed so a query can never collide with a chunk that hashes the same, and so the two are
/// distinguishable when reading the cache table by hand. The text handed in has already been
/// normalised by the requesting locale's pipeline, which means two readers typing the same question
/// with and without diacritics share one entry — and one provider call.
/// </remarks>
/// <summary>
/// The cache key for a chunk's embedding.
/// </summary>
/// <remarks>
/// Distinct from <c>ChunkHash</c>, which identifies a chunk by its heading path and body. This
/// identifies the TEXT THAT WAS EMBEDDED, which is a different thing and the source of the bug
/// below.
/// </remarks>
/// <remarks>
/// <para>
/// Hashed over the text that is actually sent to the provider, which is the normalised form — not
/// over the chunk's raw content. Those differ, and keying on the wrong one is how a cache serves a
/// vector computed from text it no longer represents.
/// </para>
/// <para>
/// <b>This is the third place the "reindex rebuilt nothing" defect lived.</b> The chunk row and
/// its tsvector are both rebuilt when a normalisation rule changes — the chunk carries a
/// normaliser hash for exactly that comparison — but the vector cache was keyed on content alone.
/// Content does not change when a rule file does, so every lookup hit, the provider was never
/// called, and the vectors stayed as they were computed under rules that no longer exist. Nothing
/// reported a problem: the reindex succeeded, the backlog was empty, and search kept answering.
/// </para>
/// <para>
/// Keying on the normalised text fixes it without a migration or a version column: change a rule,
/// the normalised text changes, the key changes, and the vector is recomputed. The cache can no
/// longer disagree with itself about what a key means.
/// </para>
/// </remarks>
public static class EmbeddingCacheKey
{
    public static string Of(string normalizedText)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes("chunk" + normalizedText), hash);

        return Convert.ToHexStringLower(hash);
    }
}

public static class QueryHash
{
    public static string Of(string text)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes("query" + text), hash);

        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>One chunk's text after normalisation, with the cache key derived from it.</summary>
internal sealed record EmbeddingWork(PendingEmbedding Pending, string NormalizedText, string CacheKey);

public sealed class EmbeddingStore
{
    private readonly NpgsqlDataSourceFactory _connections;
    private readonly SchemaTables _tables;
    private readonly EmbeddingChain _chain;
    private readonly EmbeddingModelRegistry _models;
    private readonly VectorTypeInfo _vectorType;
    private readonly NormalizerRegistry _normalizers;
    private readonly ILogger<EmbeddingStore> _logger;

    public EmbeddingStore(
        NpgsqlDataSourceFactory connections,
        SchemaTables tables,
        EmbeddingChain chain,
        EmbeddingModelRegistry models,
        VectorTypeInfo vectorType,
        NormalizerRegistry normalizers,
        ILogger<EmbeddingStore> logger)
    {
        _connections = connections;
        _tables = tables;
        _chain = chain;
        _models = models;
        _vectorType = vectorType;
        _normalizers = normalizers;
        _logger = logger;
    }

    public bool Enabled => _chain.HasProvider && _vectorType.Available;

    /// <summary>
    /// The vector for a search query, served from the cache whenever the same question has been
    /// asked before.
    /// </summary>
    /// <remarks>
    /// <b>Vector search costs a network round trip on the read path, which lexical search does
    /// not.</b> Measured against a hosted provider it took the mean search from single-digit
    /// milliseconds to several hundred — a different product, on a surface whose whole premise is
    /// that the reader gives it about four seconds. It is also a metered call on every keystroke
    /// that survives the debounce.
    /// <para>
    /// A help centre is close to the best case for caching this: the questions repeat, heavily and
    /// across users. The same table already caches chunk embeddings and is keyed by content hash,
    /// so a query is stored exactly like any other piece of text — no new schema, and one entry
    /// serves every reader who ever asks it again.
    /// </para>
    /// <para>
    /// A provider failure returns null rather than throwing: the caller drops to lexical and
    /// reports <c>degraded</c>, which is the documented behaviour for an unavailable model.
    /// </para>
    /// </remarks>
    public async Task<float[]?> EmbedQueryAsync(string slug, string text, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrEmpty(text))
        {
            return null;
        }

        string hash = QueryHash.Of(text);

        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        Dictionary<string, float[]> cached = await LoadCachedAsync(connection, slug, [hash], ct);

        if (cached.TryGetValue(hash, out float[]? hit))
        {
            NeadocsMeters.EmbeddingCacheHits.Add(1,
                new KeyValuePair<string, object?>(NeadocsTags.Model, slug));

            return hit;
        }

        try
        {
            IReadOnlyList<float[]> vectors = await _chain.EmbedAsync(slug, [text], ct);

            if (vectors.Count == 0)
            {
                return null;
            }

            await CacheAsync(connection, slug, new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                [hash] = vectors[0],
            }, ct);

            return vectors[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NeadocsMeters.ProviderFailures.Add(1,
                new KeyValuePair<string, object?>(NeadocsTags.Model, slug),
                new KeyValuePair<string, object?>(NeadocsTags.Reason, ex.GetType().Name));

            _logger.LogWarning(ex, "Embedding the query for model {Model} failed; falling back to lexical.", slug);

            return null;
        }
    }

    public async Task EmbedAsync(IReadOnlyList<PendingEmbedding> pending, CancellationToken ct)
    {
        if (!Enabled || pending.Count == 0)
        {
            return;
        }

        foreach (EmbeddingModelDescriptor model in _models.Active)
        {
            await EmbedForModelAsync(model, pending, ct);
        }
    }

    private async Task EmbedForModelAsync(
        EmbeddingModelDescriptor model, IReadOnlyList<PendingEmbedding> pending, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        // Normalise here, once, before anything is hashed or sent.
        //
        // The query side has always normalised — QueryHash documents it — and the indexing side
        // never did, so a query embedding and a chunk embedding were computed from text in two
        // different forms. Measured with the deterministic provider, every query scored 0.0000
        // against a document, including a query that was the document's own title; normalising
        // both sides took the same pair to 0.6708. A real model degrades more quietly, which is
        // worse: nothing fails, recall is simply lower than it looks.
        //
        // The key is the hash of this normalised text, not of the chunk's raw content — see
        // ChunkHash for why keying on content is what let stale vectors survive a reindex.
        EmbeddingWork[] work = new EmbeddingWork[pending.Count];

        for (int i = 0; i < pending.Count; i++)
        {
            string normalized = _normalizers.Normalize(pending[i].NormalizerTag, pending[i].Text);
            work[i] = new EmbeddingWork(pending[i], normalized, EmbeddingCacheKey.Of(normalized));
        }

        string[] keys = Array.ConvertAll(work, w => w.CacheKey);
        Dictionary<string, float[]> cached = await LoadCachedAsync(connection, model.Slug, keys, ct);

        List<EmbeddingWork> misses = [];

        foreach (EmbeddingWork item in work)
        {
            if (!cached.ContainsKey(item.CacheKey))
            {
                misses.Add(item);
            }
        }

        NeadocsMeters.EmbeddingCacheHits.Add(pending.Count - misses.Count,
            new KeyValuePair<string, object?>(NeadocsTags.Model, model.Slug));

        Dictionary<string, float[]> computed = new(StringComparer.Ordinal);

        if (misses.Count > 0)
        {
            try
            {
                List<string> texts = misses.ConvertAll(m => m.NormalizedText);
                IReadOnlyList<float[]> vectors = await _chain.EmbedAsync(model.Slug, texts, ct);

                for (int i = 0; i < misses.Count; i++)
                {
                    computed[misses[i].CacheKey] = vectors[i];
                }

                await CacheAsync(connection, model.Slug, computed, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                NeadocsMeters.ProviderFailures.Add(1,
                    new KeyValuePair<string, object?>(NeadocsTags.Model, model.Slug),
                    new KeyValuePair<string, object?>(NeadocsTags.Reason, ex.GetType().Name));

                await BacklogAsync(connection, model.Slug, misses.ConvertAll(m => m.Pending), ex.Message, ct);

                _logger.LogWarning(ex,
                    "Embedding {Count} chunk(s) for model {Model} failed; the document remains "
                    + "lexically searchable and the chunks are queued for retry.",
                    misses.Count, model.Slug);
            }
        }

        foreach (EmbeddingWork item in work)
        {
            float[]? vector = cached.TryGetValue(item.CacheKey, out float[]? hit)
                ? hit
                : computed.GetValueOrDefault(item.CacheKey);

            if (vector is not null)
            {
                await WriteVectorAsync(connection, model.Slug, item.Pending.ChunkId, vector, ct);
            }
        }
    }

    private async Task<Dictionary<string, float[]>> LoadCachedAsync(
        NpgsqlConnection connection, string slug, IReadOnlyList<string> keys, CancellationToken ct)
    {
        string[] hashes = new string[keys.Count];

        for (int i = 0; i < keys.Count; i++)
        {
            hashes[i] = keys[i];
        }

        await using NpgsqlCommand command = _connections.CreateCommand(connection,
            $"SELECT content_hash, embedding FROM {_tables.EmbeddingCache} "
            + "WHERE model_slug = @slug AND content_hash = ANY(@hashes)");
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("hashes", hashes);

        Dictionary<string, float[]> cached = new(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            cached[reader.GetString(0)] = Decode((byte[])reader[1]);
        }

        return cached;
    }

    private async Task CacheAsync(
        NpgsqlConnection connection, string slug, Dictionary<string, float[]> vectors, CancellationToken ct)
    {
        foreach (KeyValuePair<string, float[]> entry in vectors)
        {
            await using NpgsqlCommand command = _connections.CreateCommand(connection,
                $"INSERT INTO {_tables.EmbeddingCache} (content_hash, model_slug, embedding) "
                + "VALUES (@hash, @slug, @embedding) ON CONFLICT DO NOTHING");
            command.Parameters.AddWithValue("hash", entry.Key);
            command.Parameters.AddWithValue("slug", slug);
            command.Parameters.AddWithValue("embedding", NpgsqlDbType.Bytea, Encode(entry.Value));

            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task WriteVectorAsync(
        NpgsqlConnection connection, string slug, Guid chunkId, float[] vector, CancellationToken ct)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(connection,
            $"INSERT INTO {_tables.ChunkEmbeddings(slug)} (chunk_id, embedding) "
            + $"VALUES (@chunk, @embedding::{_vectorType.TypeName}) "
            + "ON CONFLICT (chunk_id) DO UPDATE SET embedding = EXCLUDED.embedding");
        command.Parameters.AddWithValue("chunk", chunkId);
        command.Parameters.AddWithValue("embedding", NpgsqlDbType.Text, Literal(vector));

        await command.ExecuteNonQueryAsync(ct);

        await using NpgsqlCommand clear = _connections.CreateCommand(connection,
            $"DELETE FROM {_tables.EmbeddingBacklog} WHERE chunk_id = @chunk AND model_slug = @slug");
        clear.Parameters.AddWithValue("chunk", chunkId);
        clear.Parameters.AddWithValue("slug", slug);

        await clear.ExecuteNonQueryAsync(ct);
    }

    private async Task BacklogAsync(
        NpgsqlConnection connection, string slug, IReadOnlyList<PendingEmbedding> chunks,
        string error, CancellationToken ct)
    {
        foreach (PendingEmbedding chunk in chunks)
        {
            await using NpgsqlCommand command = _connections.CreateCommand(connection,
                $"INSERT INTO {_tables.EmbeddingBacklog} (chunk_id, model_slug, attempts, last_error) "
                + "VALUES (@chunk, @slug, 1, @error) "
                + "ON CONFLICT (chunk_id, model_slug) DO UPDATE "
                + "SET attempts = " + _tables.EmbeddingBacklog + ".attempts + 1, "
                + "    last_error = EXCLUDED.last_error, "
                + "    next_attempt_at = now() + (interval '30 seconds' * "
                + _tables.EmbeddingBacklog + ".attempts)");
            command.Parameters.AddWithValue("chunk", chunk.ChunkId);
            command.Parameters.AddWithValue("slug", slug);
            command.Parameters.AddWithValue("error", Truncate(error));

            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<long> BacklogDepthAsync(string slug, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(connection,
            $"SELECT count(*) FROM {_tables.EmbeddingBacklog} WHERE model_slug = @slug");
        command.Parameters.AddWithValue("slug", slug);

        return (long)(await command.ExecuteScalarAsync(ct) ?? 0L);
    }

    /// <summary>
    /// Claims a batch of backlog rows for this worker and returns them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A plain SELECT here is shared, not claimed.</b> Every replica running the backlog worker
    /// selected the same due rows on the same tick, so each one embedded the same chunks. Writing
    /// the vectors is idempotent, so nothing looked broken — but two things were.
    /// </para>
    /// <para>
    /// The embedding provider is billed per call, so the cost of the backlog multiplied by the
    /// replica count. And a failure bumps <c>attempts</c>, which is checked against
    /// <c>MaxAttempts</c> — three replicas failing on the same transient provider outage spend
    /// three attempts per cycle instead of one, so a chunk exhausts a ten-attempt budget in three
    /// cycles and is then abandoned for good. No vector, no error, and search quietly degraded for
    /// that chunk forever.
    /// </para>
    /// <para>
    /// Claimed by pushing <c>next_attempt_at</c> forward — a lease — in the same statement that
    /// selects, with <c>FOR UPDATE SKIP LOCKED</c> so concurrent workers take disjoint rows rather
    /// than queueing behind each other. The row lock is held only for that short UPDATE, never
    /// across the provider call, which would tie up a connection for the length of a network round
    /// trip. A worker that dies mid-batch loses only the lease: the rows come due again when it
    /// expires.
    /// </para>
    /// </remarks>
    public async Task<List<PendingEmbedding>> DueBacklogAsync(string slug, int limit, int maxAttempts, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            WITH claimed AS (
                SELECT b.chunk_id
                FROM {_tables.EmbeddingBacklog} b
                WHERE b.model_slug = @slug
                  AND b.next_attempt_at <= now()
                  AND b.attempts < @maxAttempts
                ORDER BY b.next_attempt_at
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            ),
            leased AS (
                UPDATE {_tables.EmbeddingBacklog} b
                SET next_attempt_at = now() + make_interval(secs => @leaseSeconds)
                FROM claimed
                WHERE b.chunk_id = claimed.chunk_id AND b.model_slug = @slug
                RETURNING b.chunk_id
            )
            SELECT leased.chunk_id, c.content, c.normalizer_tag
            FROM leased
            JOIN {_tables.Chunks} c ON c.id = leased.chunk_id
            """);
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("maxAttempts", maxAttempts);

        // Long enough to cover a slow provider call for a whole batch, short enough that a worker
        // killed mid-batch does not strand its rows for long. Deliberately NOT the retry backoff:
        // this is how long the work is expected to take, not how long to wait after it fails.
        command.Parameters.AddWithValue("leaseSeconds", 300d);

        List<PendingEmbedding> due = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
                        // normalizer_tag comes from the chunk row so a retry normalises exactly as the first
            // attempt would have. Reading raw content and embedding it directly is what made the
            // retry path disagree with the query path.
            due.Add(new PendingEmbedding(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }

        return due;
    }

    public static string Literal(float[] vector)
    {
        StringBuilder builder = new(vector.Length * 8);
        builder.Append('[');

        for (int i = 0; i < vector.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        builder.Append(']');

        return builder.ToString();
    }

    public static byte[] Encode(float[] vector)
    {
        byte[] bytes = new byte[vector.Length * 4];

        for (int i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), vector[i]);
        }

        return bytes;
    }

    public static float[] Decode(byte[] bytes)
    {
        float[] vector = new float[bytes.Length / 4];

        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        }

        return vector;
    }

    private static string Truncate(string error) =>
        error.Length <= 500 ? error : error[..500];
}
