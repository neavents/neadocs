namespace Neadocs.Engine.Infrastructure.Storage;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

public sealed record ReindexDocument(
    string Key, string Locale, string Title, string Content, string? Uri,
    string Metadata, string? SourceLocale, string? SourceHash);

public sealed class ReindexService
{
    private readonly NpgsqlDataSourceFactory _connections;
    private readonly SchemaTables _tables;
    private readonly DocumentStore _store;
    private readonly JobStore _jobs;
    private readonly ILogger<ReindexService> _logger;

    private static readonly ConcurrentDictionary<Guid, byte> Running = new();

    public ReindexService(
        NpgsqlDataSourceFactory connections,
        SchemaTables tables,
        DocumentStore store,
        JobStore jobs,
        ILogger<ReindexService> logger)
    {
        _connections = connections;
        _tables = tables;
        _store = store;
        _jobs = jobs;
        _logger = logger;
    }

    public static int InFlight => Running.Count;

    public async Task<Guid?> QueueAsync(
        string tenant, string collectionKey, string? locale, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);

        Guid? collectionId = await _store.ResolveCollectionAsync(connection, null, tenant, collectionKey, ct);

        if (collectionId is null)
        {
            return null;
        }

        Guid jobId = await _jobs.CreateAsync(tenant, "reindex", ct);

        Running.TryAdd(jobId, 0);

        _ = Task.Run(() => RunAsync(jobId, tenant, collectionKey, collectionId.Value, locale), CancellationToken.None);

        return jobId;
    }

    public async Task RunAsync(
        Guid jobId, string tenant, string collectionKey, Guid collectionId, string? locale)
    {
        List<string> errors = [];
        int processed = 0;

        try
        {
            List<ReindexDocument> documents = await LoadAsync(collectionId, locale, CancellationToken.None);

            await _jobs.StartAsync(jobId, documents.Count, CancellationToken.None);

            _logger.LogInformation(
                "Reindex job {JobId} rebuilding {Count} document(s) in {Collection}.",
                jobId, documents.Count, collectionKey);

            foreach (ReindexDocument document in documents)
            {
                try
                {
                    await _store.UpsertDocumentAsync(
                        tenant, collectionKey, document.Key, document.Locale, document.Title,
                        document.Content, document.Uri, document.Metadata,
                        document.SourceLocale, document.SourceHash, force: true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    errors.Add($"{document.Key}/{document.Locale}: {ex.Message}");
                }

                processed++;

                if (processed % 25 == 0)
                {
                    await _jobs.ProgressAsync(jobId, processed, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            _logger.LogError(ex, "Reindex job {JobId} failed.", jobId);
        }
        finally
        {
            Running.TryRemove(jobId, out _);
            await _jobs.FinishAsync(jobId, processed, errors, CancellationToken.None);
        }
    }

    private async Task<List<ReindexDocument>> LoadAsync(
        Guid collectionId, string? locale, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            SELECT d.external_key, d.locale, d.title, r.content, d.source_uri, d.metadata::text,
                   d.source_locale, d.source_content_hash
            FROM {_tables.Documents} d
            JOIN {_tables.DocumentRevisions} r
              ON r.document_id = d.id AND r.revision = d.current_revision
            WHERE d.collection_id = @collection
              AND d.deleted_at IS NULL
              AND (@locale IS NULL OR d.locale = @locale)
            ORDER BY d.external_key, d.locale
            """);

        command.Parameters.AddWithValue("collection", collectionId);
        command.Parameters.AddWithValue("locale", NpgsqlDbType.Text, (object?)locale ?? DBNull.Value);

        List<ReindexDocument> documents = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            documents.Add(new ReindexDocument(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return documents;
    }
}
