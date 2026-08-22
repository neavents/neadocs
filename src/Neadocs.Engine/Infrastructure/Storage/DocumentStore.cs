namespace Neadocs.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Neadocs.Engine.Features;
using Neadocs.Engine.Infrastructure.Chunking;
using Neadocs.Engine.Infrastructure.Diagnostics;
using Neadocs.Engine.Infrastructure.Providers;
using Neadocs.Engine.Infrastructure.Text;
using Npgsql;
using NpgsqlTypes;

public sealed record CollectionRow(Guid Id, string Key, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed class DocumentStore
{
    private readonly NpgsqlDataSourceFactory _connections;
    private readonly SchemaTables _tables;
    private readonly MarkdownChunker _chunker;
    private readonly NormalizerRegistry _normalizers;
    private readonly EmbeddingStore _embeddings;
    private readonly ILogger<DocumentStore> _logger;

    public DocumentStore(
        NpgsqlDataSourceFactory connections,
        SchemaTables tables,
        MarkdownChunker chunker,
        NormalizerRegistry normalizers,
        EmbeddingStore embeddings,
        ILogger<DocumentStore> logger)
    {
        _connections = connections;
        _tables = tables;
        _chunker = chunker;
        _normalizers = normalizers;
        _embeddings = embeddings;
        _logger = logger;
    }

    public async Task<(CollectionRow Row, bool Created)> UpsertCollectionAsync(
        string tenant,
        string key,
        string name,
        string configJson,
        CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            INSERT INTO {_tables.Collections} (id, tenant_id, key, name, config)
            VALUES (@id, @tenant, @key, @name, @config::jsonb)
            ON CONFLICT (tenant_id, key) DO UPDATE
                SET name = EXCLUDED.name,
                    config = EXCLUDED.config,
                    updated_at = now()
            RETURNING id, key, name, created_at, updated_at, (xmax = 0) AS inserted
            """);

        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("config", NpgsqlDbType.Text, configJson);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        CollectionRow row = new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4));

        return (row, reader.GetBoolean(5));
    }

    public async Task<List<CollectionResponse>> ListCollectionsAsync(string tenant, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            SELECT c.id, c.key, c.name, c.created_at, c.updated_at,
                   (SELECT count(*) FROM {_tables.Documents} d
                     WHERE d.collection_id = c.id AND d.deleted_at IS NULL)
            FROM {_tables.Collections} c
            WHERE c.tenant_id = @tenant
            ORDER BY c.key
            """);
        command.Parameters.AddWithValue("tenant", tenant);

        List<CollectionResponse> items = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            items.Add(new CollectionResponse
            {
                Id = reader.GetGuid(0),
                Key = reader.GetString(1),
                Name = reader.GetString(2),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(3),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(4),
                DocumentCount = (int)reader.GetInt64(5),
            });
        }

        return items;
    }

    public async Task<bool> DeleteCollectionAsync(string tenant, string key, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(
            connection,
            $"DELETE FROM {_tables.Collections} WHERE tenant_id = @tenant AND key = @key");
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("key", key);

        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<Guid?> ResolveCollectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenant,
        string key,
        CancellationToken ct)
    {
        await using NpgsqlCommand command = transaction is null
            ? _connections.CreateCommand(connection, $"SELECT id FROM {_tables.Collections} WHERE tenant_id = @tenant AND key = @key")
            : _connections.CreateCommand(connection, transaction, $"SELECT id FROM {_tables.Collections} WHERE tenant_id = @tenant AND key = @key");

        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("key", key);

        object? result = await command.ExecuteScalarAsync(ct);

        return result is Guid id ? id : null;
    }

    public async Task<UpsertDocumentResponse?> UpsertDocumentAsync(
        string tenant,
        string collectionKey,
        string externalKey,
        string locale,
        string title,
        string content,
        string? sourceUri,
        string metadataJson,
        string? sourceLocale,
        string? sourceContentHash,
        bool force,
        CancellationToken ct)
    {
        using Activity? activity = NeadocsActivitySources.Ingest.StartActivity("document.upsert");
        activity?.SetTag(NeadocsTags.Collection, collectionKey);
        activity?.SetTag(NeadocsTags.Locale, locale);

        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        Guid? collectionId = await ResolveCollectionAsync(connection, transaction, tenant, collectionKey, ct);

        if (collectionId is null)
        {
            return null;
        }

        string documentHash = ChunkHash.OfDocument(title, content);

        (Guid documentId, int currentRevision, string? existingHash) =
            await LoadDocumentAsync(connection, transaction, collectionId.Value, externalKey, locale, ct);

        bool unchanged = existingHash is not null
            && string.Equals(existingHash, documentHash, StringComparison.Ordinal);

        if (unchanged && !force)
        {
            await transaction.CommitAsync(ct);

            NeadocsMeters.DocumentsUpserted.Add(1,
                new KeyValuePair<string, object?>(NeadocsTags.Collection, collectionKey),
                new KeyValuePair<string, object?>(NeadocsTags.Changed, false));

            int existingChunks = await CountChunksAsync(connection, null, documentId, currentRevision, ct);

            return new UpsertDocumentResponse
            {
                DocumentId = documentId,
                ExternalKey = externalKey,
                Locale = locale,
                Revision = currentRevision,
                Changed = false,
                Chunks = new ChunkCounts { Total = existingChunks, Reused = existingChunks },
            };
        }

        int revision = currentRevision + 1;

        if (documentId == Guid.Empty)
        {
            documentId = Guid.NewGuid();
            await InsertDocumentAsync(connection, transaction, documentId, collectionId.Value, externalKey,
                locale, title, sourceUri, metadataJson, revision, documentHash, sourceLocale, sourceContentHash, ct);
        }
        else
        {
            await UpdateDocumentAsync(connection, transaction, documentId, title, sourceUri, metadataJson,
                revision, documentHash, sourceLocale, sourceContentHash, ct);
        }

        await InsertRevisionAsync(connection, transaction, documentId, revision, title, content, documentHash, ct);

        IReadOnlyList<DocumentChunk> chunks = _chunker.Chunk(content);
        CompiledPipeline pipeline = _normalizers.Resolve(locale);

        Dictionary<string, ExistingChunk> existing =
            await LoadChunkHashesAsync(connection, transaction, documentId, ct);

        ChunkCounts counts = new() { Total = chunks.Count };
        HashSet<string> keep = new(StringComparer.Ordinal);
        List<PendingEmbedding> pending = [];

        foreach (DocumentChunk chunk in chunks)
        {
            keep.Add(chunk.ContentHash);

            if (existing.TryGetValue(chunk.ContentHash, out ExistingChunk match))
            {
                counts.Reused++;

                // Identical text does not imply an identical index.
                //
                // <b>This is why reindexing could not rebuild anything.</b> A chunk was reused
                // whenever its CONTENT hash matched, and reuse only repointed the row's revision
                // and ordinal — it never rewrote `tsv_folded`. But the case reindex exists for
                // (§11: "after editing a normalisation rule file") changes the normaliser, not the
                // content, so every chunk matched, every chunk was reused, and the job reported
                // success having re-indexed nothing. `force: true` did not help: it only bypasses
                // the document-level short circuit further up and never reaches this branch.
                //
                // The embedding is untouched on this path on purpose — it is derived from the
                // chunk's text, which by definition has not changed.
                if (string.Equals(match.NormalizerHash, pipeline.Hash, StringComparison.Ordinal))
                {
                    await RepointChunkAsync(connection, transaction, match.Id, revision, chunk.Ordinal, ct);
                }
                else
                {
                    await RefoldChunkAsync(
                        connection, transaction, match.Id, revision, chunk, pipeline, ct);
                }

                // A reused chunk still needs a vector, and this is the second half of why a reindex
                // could not rebuild anything.
                //
                // Only newly-created chunks were queued, so configuring an embedding model for the
                // first time and reindexing — which UI-INTEGRATION §11 names as the action for
                // exactly that — embedded nothing at all. The job reported success, the backlog sat
                // at zero because nothing had been enqueued, and the vector table stayed empty
                // while `/health/providers` showed a healthy provider. Search went on answering
                // `degraded: true` with no failure anywhere to explain it.
                //
                // Enqueuing every chunk is cheap by design: `EmbeddingStore` looks each one up in
                // the cache by content hash and only calls the provider for misses, so unchanged
                // text costs one query and no API request. It still writes the chunk→vector row,
                // which is the part that was missing.
                pending.Add(new PendingEmbedding(match.Id, chunk.ContentHash, chunk.TsvSource));

                continue;
            }

            counts.Created++;
            Guid chunkId = await InsertChunkAsync(
                connection, transaction, documentId, revision, chunk, pipeline, ct);
            pending.Add(new PendingEmbedding(chunkId, chunk.ContentHash, chunk.TsvSource));
        }

        counts.Deleted = await DeleteStaleChunksAsync(connection, transaction, documentId, keep, ct);

        await transaction.CommitAsync(ct);

        if (pending.Count > 0)
        {
            await _embeddings.EmbedAsync(pending, ct);
        }

        activity?.SetTag(NeadocsTags.ChunkCount, chunks.Count);

        NeadocsMeters.DocumentsUpserted.Add(1,
            new KeyValuePair<string, object?>(NeadocsTags.Collection, collectionKey),
            new KeyValuePair<string, object?>(NeadocsTags.Changed, true));
        NeadocsMeters.ChunksCreated.Add(counts.Created,
            new KeyValuePair<string, object?>(NeadocsTags.Collection, collectionKey));
        NeadocsMeters.ChunksDeleted.Add(counts.Deleted,
            new KeyValuePair<string, object?>(NeadocsTags.Collection, collectionKey));

        _logger.LogInformation(
            "Upserted {ExternalKey}/{Locale} in {Collection} to revision {Revision}: "
            + "{Created} chunk(s) created, {Reused} reused, {Deleted} removed.",
            externalKey, locale, collectionKey, revision, counts.Created, counts.Reused, counts.Deleted);

        return new UpsertDocumentResponse
        {
            DocumentId = documentId,
            ExternalKey = externalKey,
            Locale = locale,
            Revision = revision,
            Changed = true,
            Chunks = counts,
        };
    }

    private async Task<(Guid Id, int Revision, string? Hash)> LoadDocumentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid collectionId, string externalKey, string locale, CancellationToken ct)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(connection, transaction, $"""
            SELECT id, current_revision, content_hash, deleted_at
            FROM {_tables.Documents}
            WHERE collection_id = @collection AND external_key = @key AND locale = @locale
            """);
        command.Parameters.AddWithValue("collection", collectionId);
        command.Parameters.AddWithValue("key", externalKey);
        command.Parameters.AddWithValue("locale", locale);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return (Guid.Empty, 0, null);
        }

        bool deleted = !await reader.IsDBNullAsync(3, ct);

        return (reader.GetGuid(0), reader.GetInt32(1), deleted ? null : reader.GetString(2));
    }

    private async Task InsertDocumentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid collectionId,
        string externalKey, string locale, string title, string? sourceUri, string metadataJson,
        int revision, string hash, string? sourceLocale, string? sourceContentHash, CancellationToken ct)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(connection, transaction, $"""
            INSERT INTO {_tables.Documents}
                (id, collection_id, external_key, locale, title, source_uri, metadata,
                 current_revision, content_hash, source_locale, source_content_hash)
            VALUES (@id, @collection, @key, @locale, @title, @uri, @metadata::jsonb,
                    @revision, @hash, @sourceLocale, @sourceHash)
            ON CONFLICT (collection_id, external_key, locale) DO UPDATE
                SET title = EXCLUDED.title,
                    source_uri = EXCLUDED.source_uri,
                    metadata = EXCLUDED.metadata,
                    current_revision = EXCLUDED.current_revision,
                    content_hash = EXCLUDED.content_hash,
                    source_locale = EXCLUDED.source_locale,
                    source_content_hash = EXCLUDED.source_content_hash,
                    deleted_at = NULL,
                    updated_at = now()
            """);

        Bind(command, id, collectionId, externalKey, locale, title, sourceUri, metadataJson,
            revision, hash, sourceLocale, sourceContentHash);

        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(
        NpgsqlCommand command, Guid id, Guid collectionId, string externalKey, string locale,
        string title, string? sourceUri, string metadataJson, int revision, string hash,
        string? sourceLocale, string? sourceContentHash)
    {
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("collection", collectionId);
        command.Parameters.AddWithValue("key", externalKey);
        command.Parameters.AddWithValue("locale", locale);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("uri", (object?)sourceUri ?? DBNull.Value);
        command.Parameters.AddWithValue("metadata", NpgsqlDbType.Text, metadataJson);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("sourceLocale", (object?)sourceLocale ?? DBNull.Value);
        command.Parameters.AddWithValue("sourceHash", (object?)sourceContentHash ?? DBNull.Value);
    }

    private async Task UpdateDocumentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string title,
        string? sourceUri, string metadataJson, int revision, string hash,
        string? sourceLocale, string? sourceContentHash, CancellationToken ct)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(connection, transaction, $"""
            UPDATE {_tables.Documents}
            SET title = @title, source_uri = @uri, metadata = @metadata::jsonb,
                current_revision = @revision, content_hash = @hash,
                source_locale = @sourceLocale, source_content_hash = @sourceHash,
                deleted_at = NULL, updated_at = now()
            WHERE id = @id
            """);

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("uri", (object?)sourceUri ?? DBNull.Value);
        command.Parameters.AddWithValue("metadata", NpgsqlDbType.Text, metadataJson);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("sourceLocale", (object?)sourceLocale ?? DBNull.Value);
        command.Parameters.AddWithValue("sourceHash", (object?)sourceContentHash ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertRevisionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid documentId,
        int revision, string title, string content, string hash, CancellationToken ct)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(connection, transaction, $"""
            INSERT INTO {_tables.DocumentRevisions} (id, document_id, revision, title, content, content_hash)
            VALUES (@id, @document, @revision, @title, @content, @hash)
            ON CONFLICT (document_id, revision) DO NOTHING
            """);

        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("document", documentId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.AddWithValue("hash", hash);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A chunk already stored for this document: its row, and how it was indexed.</summary>
    private readonly record struct ExistingChunk(Guid Id, string NormalizerHash);

    private async Task<Dictionary<string, ExistingChunk>> LoadChunkHashesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid documentId, CancellationToken ct)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(connection, transaction,
            $"""
            SELECT DISTINCT ON (content_hash) content_hash, id, normalizer_hash
            FROM {_tables.Chunks}
            WHERE document_id = @document
            """);
        command.Parameters.AddWithValue("document", documentId);

        Dictionary<string, ExistingChunk> hashes = new(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            hashes[reader.GetString(0)] = new ExistingChunk(reader.GetGuid(1), reader.GetString(2));
        }

        return hashes;
    }

    /// <summary>
    /// Reuses a chunk's row and its embedding, and rebuilds only its search vector.
    /// </summary>
    /// <remarks>
    /// The middle path between repointing and reinserting, and the one that makes a reindex mean
    /// something. The text is identical, so the row and its embedding stand; the normaliser has
    /// moved, so the vector and the hash that identifies it have to be rewritten or the chunk stays
    /// indexed under rules nobody uses any more.
    /// </remarks>
    private async Task RefoldChunkAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid chunkId,
        int revision, DocumentChunk chunk, CompiledPipeline pipeline, CancellationToken ct)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(connection, transaction, $"""
            UPDATE {_tables.Chunks}
               SET revision = @revision,
                   ordinal = @ordinal,
                   tsv_folded = setweight(to_tsvector(@searchConfig::regconfig, @folded), 'A')
                                || setweight(to_tsvector('simple', @prefixes), 'D'),
                   normalizer_tag = @tag,
                   normalizer_hash = @normalizerHash
             WHERE id = @id
            """);

        string folded = pipeline.Normalize(chunk.TsvSource);

        command.Parameters.AddWithValue("id", chunkId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("ordinal", chunk.Ordinal);
        command.Parameters.AddWithValue("folded", folded);
        command.Parameters.AddWithValue("searchConfig", pipeline.SearchConfig);
        command.Parameters.AddWithValue("prefixes", pipeline.Prefixes(folded));
        command.Parameters.AddWithValue("tag", pipeline.Tag);
        command.Parameters.AddWithValue("normalizerHash", pipeline.Hash);

        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task RepointChunkAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid chunkId,
        int revision, int ordinal, CancellationToken ct)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(connection, transaction,
            $"UPDATE {_tables.Chunks} SET revision = @revision, ordinal = @ordinal WHERE id = @id");
        command.Parameters.AddWithValue("id", chunkId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("ordinal", ordinal);

        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<Guid> InsertChunkAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid documentId,
        int revision, DocumentChunk chunk, CompiledPipeline pipeline, CancellationToken ct)
    {
        Guid chunkId = Guid.NewGuid();

        await using NpgsqlCommand command = _connections.CreateCommand(connection, transaction, $"""
            INSERT INTO {_tables.Chunks}
                (id, document_id, revision, ordinal, heading_path, content, content_hash,
                 token_count, tsv_folded, normalizer_tag, normalizer_hash)
            VALUES (@id, @document, @revision, @ordinal, @path::jsonb, @content, @hash,
                    @tokens,
                    setweight(to_tsvector(@searchConfig::regconfig, @folded), 'A')
                      || setweight(to_tsvector('simple', @prefixes), 'D'),
                    @tag, @normalizerHash)
            ON CONFLICT (document_id, revision, ordinal) DO UPDATE
                SET heading_path = EXCLUDED.heading_path,
                    content = EXCLUDED.content,
                    content_hash = EXCLUDED.content_hash,
                    token_count = EXCLUDED.token_count,
                    tsv_folded = EXCLUDED.tsv_folded,
                    normalizer_tag = EXCLUDED.normalizer_tag,
                    normalizer_hash = EXCLUDED.normalizer_hash
            RETURNING id
            """);

        command.Parameters.AddWithValue("id", chunkId);
        command.Parameters.AddWithValue("document", documentId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("ordinal", chunk.Ordinal);
        command.Parameters.AddWithValue("path", NpgsqlDbType.Text, JsonArray(chunk.HeadingPath));
        command.Parameters.AddWithValue("content", chunk.Content);
        command.Parameters.AddWithValue("hash", chunk.ContentHash);
        string folded = pipeline.Normalize(chunk.TsvSource);

        command.Parameters.AddWithValue("tokens", chunk.TokenCount);
        command.Parameters.AddWithValue("folded", folded);
        // Sent as a parameter and cast, never interpolated: a rule set is a file on disk and this
        // would otherwise be the one place its contents reach SQL as text.
        command.Parameters.AddWithValue("searchConfig", pipeline.SearchConfig);
        command.Parameters.AddWithValue("prefixes", pipeline.Prefixes(folded));
        command.Parameters.AddWithValue("tag", pipeline.Tag);
        command.Parameters.AddWithValue("normalizerHash", pipeline.Hash);

        object? returned = await command.ExecuteScalarAsync(ct);

        return returned is Guid id ? id : chunkId;
    }

    private async Task<int> DeleteStaleChunksAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid documentId,
        HashSet<string> keep, CancellationToken ct)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(connection, transaction,
            $"DELETE FROM {_tables.Chunks} WHERE document_id = @document AND NOT (content_hash = ANY(@keep))");
        command.Parameters.AddWithValue("document", documentId);
        command.Parameters.AddWithValue("keep", keep.ToArray());

        return await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> CountChunksAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid documentId, int revision, CancellationToken ct)
    {
        await using NpgsqlCommand command = transaction is null
            ? _connections.CreateCommand(connection, $"SELECT count(*) FROM {_tables.Chunks} WHERE document_id = @document AND revision = @revision")
            : _connections.CreateCommand(connection, transaction, $"SELECT count(*) FROM {_tables.Chunks} WHERE document_id = @document AND revision = @revision");

        command.Parameters.AddWithValue("document", documentId);
        command.Parameters.AddWithValue("revision", revision);

        return (int)(long)(await command.ExecuteScalarAsync(ct) ?? 0L);
    }

    internal static string JsonArray(IReadOnlyList<string> values)
    {
        System.Text.StringBuilder builder = new();
        builder.Append('[');

        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(System.Text.Json.JsonSerializer.Serialize(
                values[i], Neadocs.Engine.Infrastructure.Serialization.NeadocsJsonContext.Default.String));
        }

        builder.Append(']');

        return builder.ToString();
    }
}
