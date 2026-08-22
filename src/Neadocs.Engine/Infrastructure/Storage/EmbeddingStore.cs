namespace Neadocs.Engine.Infrastructure.Storage;

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

public sealed record PendingEmbedding(Guid ChunkId, string ContentHash, string Text);

/// <summary>
/// The cache key for a search query's embedding.
/// </summary>
/// <remarks>
/// Prefixed so a query can never collide with a chunk that hashes the same, and so the two are
/// distinguishable when reading the cache table by hand. The text handed in has already been
/// normalised by the requesting locale's pipeline, which means two readers typing the same question
/// with and without diacritics share one entry — and one provider call.
/// </remarks>
public static class QueryHash
{
    public static string Of(string text)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes("query" + text), hash);

        return Convert.ToHexStringLower(hash);
    }
}

public sealed class EmbeddingStore
{
    private readonly NpgsqlDataSourceFactory _connections;
    private readonly SchemaTables _tables;
    private readonly EmbeddingChain _chain;
    private readonly EmbeddingModelRegistry _models;
    private readonly VectorTypeInfo _vectorType;
    private readonly ILogger<EmbeddingStore> _logger;

    public EmbeddingStore(
        NpgsqlDataSourceFactory connections,
        SchemaTables tables,
        EmbeddingChain chain,
        EmbeddingModelRegistry models,
        VectorTypeInfo vectorType,
        ILogger<EmbeddingStore> logger)
    {
        _connections = connections;
        _tables = tables;
        _chain = chain;
        _models = models;
        _vectorType = vectorType;
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

        PendingEmbedding probe = new(Guid.Empty, hash, text);
        Dictionary<string, float[]> cached = await LoadCachedAsync(connection, slug, [probe], ct);

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

        Dictionary<string, float[]> cached = await LoadCachedAsync(connection, model.Slug, pending, ct);

        List<PendingEmbedding> misses = [];

        foreach (PendingEmbedding item in pending)
        {
            if (!cached.ContainsKey(item.ContentHash))
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
                List<string> texts = misses.ConvertAll(m => m.Text);
                IReadOnlyList<float[]> vectors = await _chain.EmbedAsync(model.Slug, texts, ct);

                for (int i = 0; i < misses.Count; i++)
                {
                    computed[misses[i].ContentHash] = vectors[i];
                }

                await CacheAsync(connection, model.Slug, computed, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                NeadocsMeters.ProviderFailures.Add(1,
                    new KeyValuePair<string, object?>(NeadocsTags.Model, model.Slug),
                    new KeyValuePair<string, object?>(NeadocsTags.Reason, ex.GetType().Name));

                await BacklogAsync(connection, model.Slug, misses, ex.Message, ct);

                _logger.LogWarning(ex,
                    "Embedding {Count} chunk(s) for model {Model} failed; the document remains "
                    + "lexically searchable and the chunks are queued for retry.",
                    misses.Count, model.Slug);
            }
        }

        foreach (PendingEmbedding item in pending)
        {
            float[]? vector = cached.TryGetValue(item.ContentHash, out float[]? hit)
                ? hit
                : computed.GetValueOrDefault(item.ContentHash);

            if (vector is not null)
            {
                await WriteVectorAsync(connection, model.Slug, item.ChunkId, vector, ct);
            }
        }
    }

    private async Task<Dictionary<string, float[]>> LoadCachedAsync(
        NpgsqlConnection connection, string slug, IReadOnlyList<PendingEmbedding> pending, CancellationToken ct)
    {
        string[] hashes = new string[pending.Count];

        for (int i = 0; i < pending.Count; i++)
        {
            hashes[i] = pending[i].ContentHash;
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

    public async Task<List<PendingEmbedding>> DueBacklogAsync(string slug, int limit, int maxAttempts, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            SELECT b.chunk_id, c.content_hash, c.content
            FROM {_tables.EmbeddingBacklog} b
            JOIN {_tables.Chunks} c ON c.id = b.chunk_id
            WHERE b.model_slug = @slug
              AND b.next_attempt_at <= now()
              AND b.attempts < @maxAttempts
            ORDER BY b.next_attempt_at
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("maxAttempts", maxAttempts);

        List<PendingEmbedding> due = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
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
