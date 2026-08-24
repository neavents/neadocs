namespace Neadocs.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Neadocs.Engine.Features;
using Neadocs.Engine.Infrastructure.Text;
using Npgsql;
using NpgsqlTypes;

public sealed class DocumentReader
{
    private const char CursorSeparator = (char)0x1F;

    private readonly NpgsqlDataSourceFactory _connections;
    private readonly SchemaTables _tables;
    private readonly DocumentStore _store;
    private readonly NormalizerRegistry _normalizers;

    public DocumentReader(
        NpgsqlDataSourceFactory connections,
        SchemaTables tables,
        DocumentStore store,
        NormalizerRegistry normalizers)
    {
        _connections = connections;
        _tables = tables;
        _store = store;
        _normalizers = normalizers;
    }

    /// <summary>
    /// The hash each rule set currently produces, keyed by the tag it is registered under.
    /// </summary>
    /// <remarks>
    /// The fallback tag is excluded: it is not a locale and would never match a document's
    /// <c>locale</c> column. Documents in a locale with no rule set of its own fall through to the
    /// fallback hash in the query's COALESCE, which is exactly what indexed them.
    /// </remarks>
    private (string[] Locales, string[] Hashes) ExpectedHashes()
    {
        List<string> locales = [];
        List<string> hashes = [];

        foreach (string tag in _normalizers.Tags)
        {
            if (string.Equals(tag, RuleOperations.FallbackTag, StringComparison.Ordinal))
            {
                continue;
            }

            locales.Add(tag);
            hashes.Add(_normalizers.Describe(tag).Pipeline.Hash);
        }

        return ([.. locales], [.. hashes]);
    }

    public async Task<DocumentListResponse?> ListAsync(
        string tenant, string collectionKey, string? locale, string? staleAgainst,
        string? cursor, int limit, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenReadAsync(ct);

        Guid? collectionId = await _store.ResolveCollectionAsync(connection, null, tenant, collectionKey, ct);

        if (collectionId is null)
        {
            return null;
        }

        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            SELECT d.id, d.external_key, d.locale, d.title, d.source_uri, d.current_revision,
                   d.content_hash, d.source_locale, d.source_content_hash, d.metadata::text,
                   d.created_at, d.updated_at,
                   (SELECT count(*) FROM {_tables.Chunks} c
                     WHERE c.document_id = d.id AND c.revision = d.current_revision),
                   CASE WHEN @stale IS NULL OR d.source_content_hash IS NULL THEN FALSE
                        ELSE d.source_content_hash IS DISTINCT FROM (
                            SELECT s.content_hash FROM {_tables.Documents} s
                            WHERE s.collection_id = d.collection_id
                              AND s.external_key = d.external_key
                              AND s.locale = @stale
                              AND s.deleted_at IS NULL)
                   END AS stale
            FROM {_tables.Documents} d
            WHERE d.collection_id = @collection
              AND d.deleted_at IS NULL
              AND (@locale IS NULL OR d.locale = @locale)
              AND (@cursor IS NULL OR (d.external_key, d.locale) > (@cursorKey, @cursorLocale))
            ORDER BY d.external_key, d.locale
            LIMIT @limit
            """);

        (string? cursorKey, string? cursorLocale) = SplitCursor(cursor);

        command.Parameters.AddWithValue("collection", collectionId.Value);
        command.Parameters.AddWithValue("locale", NpgsqlDbType.Text, (object?)locale ?? DBNull.Value);
        command.Parameters.AddWithValue("stale", NpgsqlDbType.Text, (object?)staleAgainst ?? DBNull.Value);
        command.Parameters.AddWithValue("cursor", NpgsqlDbType.Text, (object?)cursor ?? DBNull.Value);
        command.Parameters.AddWithValue("cursorKey", (object?)cursorKey ?? string.Empty);
        command.Parameters.AddWithValue("cursorLocale", (object?)cursorLocale ?? string.Empty);
        command.Parameters.AddWithValue("limit", limit);

        DocumentListResponse response = new();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            response.Items.Add(Map(reader));
        }

        if (response.Items.Count == limit)
        {
            DocumentResponse last = response.Items[^1];
            response.NextCursor = last.ExternalKey + CursorSeparator + last.Locale;
        }

        if (staleAgainst is not null)
        {
            response.Items.RemoveAll(d => !d.Stale);
        }

        return response;
    }

    public async Task<(DocumentResponse? Document, int Matches)> GetAsync(
        string tenant, string collectionKey, string externalKey, string? locale, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenReadAsync(ct);

        Guid? collectionId = await _store.ResolveCollectionAsync(connection, null, tenant, collectionKey, ct);

        if (collectionId is null)
        {
            return (null, 0);
        }

        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            SELECT d.id, d.external_key, d.locale, d.title, d.source_uri, d.current_revision,
                   d.content_hash, d.source_locale, d.source_content_hash, d.metadata::text,
                   d.created_at, d.updated_at,
                   (SELECT count(*) FROM {_tables.Chunks} c
                     WHERE c.document_id = d.id AND c.revision = d.current_revision),
                   FALSE,
                   (SELECT r.content FROM {_tables.DocumentRevisions} r
                     WHERE r.document_id = d.id AND r.revision = d.current_revision)
            FROM {_tables.Documents} d
            WHERE d.collection_id = @collection
              AND d.external_key = @key
              AND d.deleted_at IS NULL
              AND (@locale IS NULL OR d.locale = @locale)
            ORDER BY d.locale
            """);

        command.Parameters.AddWithValue("collection", collectionId.Value);
        command.Parameters.AddWithValue("key", externalKey);
        command.Parameters.AddWithValue("locale", NpgsqlDbType.Text, (object?)locale ?? DBNull.Value);

        List<DocumentResponse> matches = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            DocumentResponse document = Map(reader);
            document.Content = await reader.IsDBNullAsync(14, ct) ? null : reader.GetString(14);
            matches.Add(document);
        }

        return matches.Count switch
        {
            0 => (null, 0),
            1 => (matches[0], 1),
            _ => (null, matches.Count),
        };
    }

    public async Task<int> SoftDeleteAsync(
        string tenant, string collectionKey, string externalKey, string? locale, CancellationToken ct)
    {
        // OpenAsync, not OpenReadAsync, and it is the only method in this file that is. Everything
        // else here reads; this one UPDATEs. The class is called DocumentReader, so a later sweep
        // that routes "the reader" to the standby wholesale would land exactly here — and a
        // standby rejects the write, so the delete would start failing at runtime for a reason the
        // file name actively argues against.
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        Guid? collectionId = await _store.ResolveCollectionAsync(connection, null, tenant, collectionKey, ct);

        if (collectionId is null)
        {
            return 0;
        }

        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            UPDATE {_tables.Documents}
            SET deleted_at = now(), updated_at = now()
            WHERE collection_id = @collection AND external_key = @key
              AND deleted_at IS NULL
              AND (@locale IS NULL OR locale = @locale)
            """);

        command.Parameters.AddWithValue("collection", collectionId.Value);
        command.Parameters.AddWithValue("key", externalKey);
        command.Parameters.AddWithValue("locale", NpgsqlDbType.Text, (object?)locale ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<RevisionListResponse?> ListRevisionsAsync(
        string tenant, string collectionKey, string externalKey, string? locale, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenReadAsync(ct);

        Guid? collectionId = await _store.ResolveCollectionAsync(connection, null, tenant, collectionKey, ct);

        if (collectionId is null)
        {
            return null;
        }

        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            SELECT r.revision, r.title, r.content_hash, length(r.content), r.created_at
            FROM {_tables.DocumentRevisions} r
            JOIN {_tables.Documents} d ON d.id = r.document_id
            WHERE d.collection_id = @collection AND d.external_key = @key
              AND (@locale IS NULL OR d.locale = @locale)
            ORDER BY r.revision DESC
            """);

        command.Parameters.AddWithValue("collection", collectionId.Value);
        command.Parameters.AddWithValue("key", externalKey);
        command.Parameters.AddWithValue("locale", NpgsqlDbType.Text, (object?)locale ?? DBNull.Value);

        RevisionListResponse response = new();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            response.Items.Add(new RevisionResponse
            {
                Revision = reader.GetInt32(0),
                Title = reader.GetString(1),
                ContentHash = reader.GetString(2),
                Length = reader.GetInt32(3),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(4),
            });
        }

        return response.Items.Count == 0 ? null : response;
    }

    public async Task<StatsResponse> StatsAsync(string tenant, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenReadAsync(ct);

        StatsResponse stats = new() { Schema = _tables.Name };

        await using (NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            SELECT c.key,
                   count(DISTINCT d.id) FILTER (WHERE d.deleted_at IS NULL),
                   count(ch.id)
            FROM {_tables.Collections} c
            LEFT JOIN {_tables.Documents} d ON d.collection_id = c.id
            LEFT JOIN {_tables.Chunks} ch ON ch.document_id = d.id AND ch.revision = d.current_revision
            WHERE c.tenant_id = @tenant
            GROUP BY c.key
            ORDER BY c.key
            """))
        {
            command.Parameters.AddWithValue("tenant", tenant);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                stats.Collections.Add(new CollectionStats
                {
                    Key = reader.GetString(0),
                    DocumentCount = (int)reader.GetInt64(1),
                    ChunkCount = (int)reader.GetInt64(2),
                });
            }
        }

        // `staleChunks` is what tells an operator a reindex is due after a normalisation rule
        // changed — UI-INTEGRATION §11 names it as the signal to act on.
        //
        // It reported zero for every locale, always, for two independent reasons: the expected hash
        // was bound to the empty string, so the FILTER counted every chunk whose hash was not "" —
        // that is, all of them — and the resulting column was then never read, so the count was
        // discarded and the field left at its default. Either one alone would have produced a
        // permanent zero, which is the least alarming wrong answer available: an operator watching
        // this number sees a corpus that is never stale and never needs rebuilding.
        //
        // The expected hash is per locale, because the pipeline is. It is resolved for each locale
        // present rather than passed as one value, so a change to one language's rules does not
        // report every other language stale.
        await using (NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            SELECT d.locale,
                   count(DISTINCT d.id),
                   count(ch.id),
                   count(ch.id) FILTER (
                       WHERE ch.normalizer_hash IS DISTINCT FROM COALESCE(e.hash, @fallbackHash))
            FROM {_tables.Documents} d
            JOIN {_tables.Collections} c ON c.id = d.collection_id AND c.tenant_id = @tenant
            LEFT JOIN {_tables.Chunks} ch ON ch.document_id = d.id AND ch.revision = d.current_revision
            LEFT JOIN unnest(@expectedLocales, @expectedHashes) AS e(loc, hash) ON e.loc = d.locale
            WHERE d.deleted_at IS NULL
            GROUP BY d.locale, e.hash
            ORDER BY d.locale
            """))
        {
            (string[] expectedLocales, string[] expectedHashes) = ExpectedHashes();

            command.Parameters.AddWithValue("tenant", tenant);
            command.Parameters.AddWithValue("expectedLocales", expectedLocales);
            command.Parameters.AddWithValue("expectedHashes", expectedHashes);
            command.Parameters.AddWithValue("fallbackHash", _normalizers.Resolve(null).Hash);

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                stats.Locales.Add(new LocaleStats
                {
                    Locale = reader.GetString(0),
                    DocumentCount = (int)reader.GetInt64(1),
                    ChunkCount = (int)reader.GetInt64(2),
                    StaleChunks = (int)reader.GetInt64(3),
                });
            }
        }

        foreach (CollectionStats collection in stats.Collections)
        {
            stats.DocumentCount += collection.DocumentCount;
            stats.ChunkCount += collection.ChunkCount;
        }

        stats.CollectionCount = stats.Collections.Count;

        await using (NpgsqlCommand command = _connections.CreateCommand(
            connection, $"SELECT count(*) FROM {_tables.EmbeddingBacklog}"))
        {
            stats.BacklogDepth = (int)(long)(await command.ExecuteScalarAsync(ct) ?? 0L);
        }

        return stats;
    }

    public async Task<int> StaleChunkCountAsync(string normalizerTag, string expectedHash, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenReadAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(connection,
            $"SELECT count(*) FROM {_tables.Chunks} WHERE normalizer_tag = @tag AND normalizer_hash <> @hash");
        command.Parameters.AddWithValue("tag", normalizerTag);
        command.Parameters.AddWithValue("hash", expectedHash);

        return (int)(long)(await command.ExecuteScalarAsync(ct) ?? 0L);
    }

    private static (string?, string?) SplitCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, null);
        }

        int separator = cursor.IndexOf(CursorSeparator);

        return separator < 0
            ? (cursor, string.Empty)
            : (cursor[..separator], cursor[(separator + 1)..]);
    }

    private static DocumentResponse Map(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        ExternalKey = reader.GetString(1),
        Locale = reader.GetString(2),
        Title = reader.GetString(3),
        SourceUri = reader.IsDBNull(4) ? null : reader.GetString(4),
        Revision = reader.GetInt32(5),
        ContentHash = reader.GetString(6),
        SourceLocale = reader.IsDBNull(7) ? null : reader.GetString(7),
        SourceContentHash = reader.IsDBNull(8) ? null : reader.GetString(8),
        Metadata = JsonDocument.Parse(reader.GetString(9)).RootElement.Clone(),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(10),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(11),
        ChunkCount = (int)reader.GetInt64(12),
        Stale = !reader.IsDBNull(13) && reader.GetBoolean(13),
    };
}
