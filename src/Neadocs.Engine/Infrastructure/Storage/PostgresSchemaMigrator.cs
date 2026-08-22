namespace Neadocs.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Diagnostics;
using Neadocs.Engine.Infrastructure.Providers;
using Npgsql;

public sealed class PostgresSchemaMigrator : IHostedService
{
    private const string TrigramExtension = "pg_trgm";
    private const string VectorExtension = "vector";

    // Extensions are database-global objects and are created in a database-global place.
    //
    // They used to be created in the engine's own schema, which is correct for a deployment and
    // wrong for anything else: the test suite runs in a throwaway schema, so the extension got
    // captured inside something designed to be dropped. Dropping it then orphaned every HNSW
    // index that referenced the access method, leaving schemas that DROP CASCADE cannot remove.
    private const string ExtensionSchema = "public";

    private readonly NpgsqlDataSourceFactory _connections;
    private readonly SchemaTables _tables;
    private readonly DocumentEngineOptions _options;
    private readonly EmbeddingModelRegistry _models;
    private readonly VectorTypeInfo _vectorType;
    private readonly MigrationState _state;
    private readonly ILogger<PostgresSchemaMigrator> _logger;

    public PostgresSchemaMigrator(
        NpgsqlDataSourceFactory connections,
        SchemaTables tables,
        IOptions<DocumentEngineOptions> options,
        EmbeddingModelRegistry models,
        VectorTypeInfo vectorType,
        MigrationState state,
        ILogger<PostgresSchemaMigrator> logger)
    {
        _connections = connections;
        _tables = tables;
        _options = options.Value;
        _models = models;
        _vectorType = vectorType;
        _state = state;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => MigrateAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        using Activity? activity = NeadocsActivitySources.Migration.StartActivity("schema.migrate");
        activity?.SetTag(NeadocsTags.Schema, _tables.Name);

        long started = Stopwatch.GetTimestamp();

        _logger.LogInformation(
            "Schema migration starting for schema {Schema}.",
            _tables.Name);

        await using NpgsqlConnection connection = await _connections.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await AcquireAdvisoryLockAsync(connection, transaction, cancellationToken);

        await CreateSchemaAsync(connection, transaction, cancellationToken);

        string trigramSchema = await EnsureTrigramExtensionAsync(connection, transaction, cancellationToken);
        _vectorType.ResolveTrigram(trigramSchema);

        string? vectorSchema = await EnsureVectorExtensionAsync(connection, transaction, cancellationToken);

        int statements = 0;

        foreach (string ddl in SchemaDdl.FixedTables(_tables))
        {
            await ExecuteAsync(connection, transaction, ddl, cancellationToken);
            statements++;
        }

        foreach (string ddl in SchemaDdl.Indexes(_tables, trigramSchema))
        {
            await ExecuteAsync(connection, transaction, ddl, cancellationToken);
            statements++;
        }

        // Reconcile whenever pgvector exists at all, not only when a model is configured.
        // Removing the last model from configuration is precisely the case the orphan guard is
        // there for, and gating on "we need vectors" skipped it exactly then.
        vectorSchema ??= await ExtensionSchemaAsync(
            connection, transaction, VectorExtension, cancellationToken);

        if (vectorSchema is not null)
        {
            if (_models.HasActiveModel)
            {
                _vectorType.Resolve(vectorSchema);
            }

            EmbeddingTableMigrator embeddings = new(_connections, _tables, _models, _logger);
            await embeddings.ReconcileAsync(connection, transaction, vectorSchema, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        _state.MarkCompleted();

        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        activity?.SetTag("neadocs.statement_count", statements);

        _logger.LogInformation(
            "Schema migration completed for schema {Schema}: {StatementCount} statements in {ElapsedMs}ms.",
            _tables.Name,
            statements,
            (long)elapsed.TotalMilliseconds);
    }

    private async Task AcquireAdvisoryLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        long key = SchemaDdl.AdvisoryLockKey(_tables.Name);

        await using NpgsqlCommand command =
            _connections.CreateCommand(connection, transaction, "SELECT pg_advisory_xact_lock(@key)");
        command.Parameters.AddWithValue("key", key);

        await command.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogDebug(
            "Migration advisory lock {LockKey} held for schema {Schema}.",
            key,
            _tables.Name);
    }

    private async Task CreateSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            connection,
            transaction,
            $"CREATE SCHEMA IF NOT EXISTS {_tables.Name}",
            cancellationToken);

    private async Task<string> EnsureTrigramExtensionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        string? existing = await ExtensionSchemaAsync(
            connection, transaction, TrigramExtension, cancellationToken);

        if (existing is not null)
        {
            _logger.LogDebug(
                "Extension {Extension} already present in schema {ExtensionSchema}.",
                TrigramExtension,
                existing);

            return existing;
        }

        try
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"CREATE EXTENSION IF NOT EXISTS {TrigramExtension} WITH SCHEMA {ExtensionSchema}",
                cancellationToken);
        }
        catch (PostgresException ex)
        {
            throw new InvalidOperationException(
                $"The '{TrigramExtension}' extension is required and could not be created in "
                + $"schema '{ExtensionSchema}': {ex.MessageText}. Install it as a superuser with "
                + $"CREATE EXTENSION {TrigramExtension}; or grant this role permission to do so.",
                ex);
        }

        string? created = await ExtensionSchemaAsync(
            connection, transaction, TrigramExtension, cancellationToken);

        if (created is null)
        {
            throw new InvalidOperationException(
                $"The '{TrigramExtension}' extension reported success but is not registered in "
                + "pg_extension. Refusing to build a trigram index that would not work.");
        }

        _logger.LogInformation(
            "Created extension {Extension} in schema {ExtensionSchema}.",
            TrigramExtension,
            created);

        return created;
    }

    private async Task<string?> EnsureVectorExtensionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        List<EmbeddingModelOptions> active =
            _options.EmbeddingModels.Where(m => !m.Retired).ToList();

        if (active.Count == 0)
        {
            _logger.LogInformation(
                "No embedding model is configured; skipping the {Extension} extension and running "
                + "lexical-only.",
                VectorExtension);
            return null;
        }

        string? existing = await ExtensionSchemaAsync(
            connection, transaction, VectorExtension, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        try
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"CREATE EXTENSION IF NOT EXISTS {VectorExtension} WITH SCHEMA {ExtensionSchema}",
                cancellationToken);
        }
        catch (PostgresException ex)
        {
            string models = string.Join(", ", active.Select(m => m.Model));

            throw new InvalidOperationException(
                $"DocumentEngine:EmbeddingModels configures {active.Count} model(s) ({models}), "
                + $"which require the '{VectorExtension}' (pgvector) extension, and it could not "
                + $"be created: {ex.MessageText}. Either install pgvector — the stock postgres "
                + "image does not ship it, so use an image such as pgvector/pgvector:pg17 — or "
                + "set DocumentEngine:EmbeddingModels to [] to run lexical-only. Refusing to "
                + "start: an operator who configured a model expects vectors, so falling back "
                + "silently would be the worse failure.",
                ex);
        }

        return await ExtensionSchemaAsync(connection, transaction, VectorExtension, cancellationToken)
            ?? throw new InvalidOperationException(
                $"The '{VectorExtension}' extension reported success but is not registered in "
                + "pg_extension.");
    }

    private async Task<string?> ExtensionSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string extensionName,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(
            connection,
            transaction,
            """
            SELECT n.nspname
            FROM pg_extension e
            JOIN pg_namespace n ON n.oid = e.extnamespace
            WHERE e.extname = @name
            """);
        command.Parameters.AddWithValue("name", extensionName);

        object? result = await command.ExecuteScalarAsync(cancellationToken);

        return result as string;
    }

    private async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(connection, transaction, sql);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
