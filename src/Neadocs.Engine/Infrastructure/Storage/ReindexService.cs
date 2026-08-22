namespace Neadocs.Engine.Infrastructure.Storage;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
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

    private readonly IHostApplicationLifetime _lifetime;

    public ReindexService(
        NpgsqlDataSourceFactory connections,
        SchemaTables tables,
        DocumentStore store,
        JobStore jobs,
        IHostApplicationLifetime lifetime,
        ILogger<ReindexService> logger)
    {
        _connections = connections;
        _tables = tables;
        _store = store;
        _jobs = jobs;
        _lifetime = lifetime;
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

        // Tied to the host's shutdown, not detached from it. A reindex rebuilds and re-embeds
        // every document in a collection, so it runs for a long time — and it is exactly what
        // someone runs after editing a normalisation rule, which is exactly when a deploy might
        // also be rolling. Started with CancellationToken.None it kept going into a terminating
        // process, was killed mid-document, and left its job row saying "running" with nothing
        // anywhere that would ever move it. A caller polling that id waits forever.
        _ = Task.Run(
            () => RunAsync(jobId, tenant, collectionKey, collectionId.Value, locale, _lifetime.ApplicationStopping),
            CancellationToken.None);

        return jobId;
    }

    public async Task RunAsync(
        Guid jobId, string tenant, string collectionKey, Guid collectionId, string? locale,
        CancellationToken ct = default)
    {
        List<string> errors = [];
        int processed = 0;

        try
        {
            List<ReindexDocument> documents = await LoadAsync(collectionId, locale, ct);

            await _jobs.StartAsync(jobId, documents.Count, CancellationToken.None);

            _logger.LogInformation(
                "Reindex job {JobId} rebuilding {Count} document(s) in {Collection}.",
                jobId, documents.Count, collectionKey);

            foreach (ReindexDocument document in documents)
            {
                // Between documents, not inside one. A document's rebuild is transactional, so
                // stopping between them leaves the collection consistent — partly rebuilt, which
                // the next reindex finishes — rather than a document half-chunked.
                ct.ThrowIfCancellationRequested();

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
        catch (OperationCanceledException)
        {
            // Recorded as a real outcome rather than left dangling. "Stopped after 240 of 900"
            // tells an operator to run it again; a row that still says "running" tells them
            // nothing and never changes.
            errors.Add(
                $"The engine shut down after {processed} document(s); the reindex did not finish. Run it again.");

            _logger.LogWarning(
                "Reindex job {JobId} stopped at {Processed} document(s) because the host is shutting down.",
                jobId, processed);
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
