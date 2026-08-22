namespace Neadocs.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Neadocs.Engine.Infrastructure.Providers;
using Npgsql;

public sealed class EmbeddingTableMigrator
{
    private readonly NpgsqlDataSourceFactory _connections;
    private readonly SchemaTables _tables;
    private readonly EmbeddingModelRegistry _models;
    private readonly ILogger _logger;

    public EmbeddingTableMigrator(
        NpgsqlDataSourceFactory connections,
        SchemaTables tables,
        EmbeddingModelRegistry models,
        ILogger logger)
    {
        _connections = connections;
        _tables = tables;
        _models = models;
        _logger = logger;
    }

    public async Task ReconcileAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string vectorSchema,
        CancellationToken ct)
    {
        Dictionary<string, int> present = await ExistingTablesAsync(connection, transaction, ct);

        foreach (EmbeddingModelDescriptor model in _models.All)
        {
            string table = SchemaTables.EmbeddingTableName(model.Slug);

            if (!present.TryGetValue(table, out int storedDimensions))
            {
                if (model.Retired)
                {
                    continue;
                }

                await CreateAsync(connection, transaction, model, vectorSchema, ct);
                _logger.LogInformation(
                    "Created embedding table {Table} with {Dimensions} dimensions.", table, model.Dimensions);
                continue;
            }

            if (storedDimensions != model.Dimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding table {_tables.Name}.{table} stores vectors of {storedDimensions} "
                    + $"dimensions but DocumentEngine:EmbeddingModels declares {model.Dimensions} "
                    + $"for model '{model.Model}'. Refusing to start: writing a different width "
                    + "into an existing index corrupts it silently. Either restore the configured "
                    + $"value to {storedDimensions}, or drop {_tables.Name}.{table} and reindex.");
            }
        }

        foreach (KeyValuePair<string, int> orphan in present)
        {
            string slug = SchemaTables.SlugFromEmbeddingTableName(orphan.Key);

            if (_models.BySlug(slug) is not null)
            {
                continue;
            }

            long rows = await CountRowsAsync(connection, transaction, orphan.Key, ct);

            if (rows == 0)
            {
                await ExecuteAsync(connection, transaction,
                    $"DROP TABLE IF EXISTS {_tables.Name}.{orphan.Key}", ct);
                _logger.LogInformation("Dropped empty orphaned embedding table {Table}.", orphan.Key);
                continue;
            }

            throw new InvalidOperationException(
                $"Embedding table {_tables.Name}.{orphan.Key} holds {rows} row(s) but no entry in "
                + $"DocumentEngine:EmbeddingModels produces the slug '{slug}'. Refusing to start: "
                + "removing a configuration line must never silently destroy an index someone paid "
                + "to build. Restore the entry, or declare it with \"Retired\": true to stop "
                + "writing while keeping the data.");
        }
    }

    private async Task CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EmbeddingModelDescriptor model,
        string vectorSchema,
        CancellationToken ct)
    {
        string table = $"{_tables.Name}.{SchemaTables.EmbeddingTableName(model.Slug)}";

        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {table} (
                chunk_id   uuid PRIMARY KEY REFERENCES {_tables.Chunks}(id) ON DELETE CASCADE,
                embedding  {vectorSchema}.vector({model.Dimensions}) NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            )
            """, ct);

        await ExecuteAsync(connection, transaction, $"""
            CREATE INDEX IF NOT EXISTS ix_emb_{model.Slug}_hnsw ON {table}
            USING hnsw (embedding {vectorSchema}.vector_cosine_ops) WITH (m = 16, ef_construction = 64)
            """, ct);
    }

    private async Task<Dictionary<string, int>> ExistingTablesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(connection, transaction, """
            SELECT c.relname, a.atttypmod
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'embedding'
            WHERE n.nspname = @schema
              AND c.relkind = 'r'
              AND c.relname LIKE @prefix
            """);

        command.Parameters.AddWithValue("schema", _tables.Name);
        command.Parameters.AddWithValue("prefix", SchemaTables.EmbeddingTablePrefix + "%");

        Dictionary<string, int> tables = new(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            tables[reader.GetString(0)] = reader.GetInt32(1);
        }

        return tables;
    }

    private async Task<long> CountRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string table, CancellationToken ct)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(
            connection, transaction, $"SELECT count(*) FROM {_tables.Name}.{table}");

        return (long)(await command.ExecuteScalarAsync(ct) ?? 0L);
    }

    private async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using NpgsqlCommand command = _connections.CreateCommand(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(ct);
    }
}
