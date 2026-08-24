namespace Neadocs.Engine.Infrastructure.Retrieval;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Neadocs.Engine.Infrastructure.Storage;
using Npgsql;

public sealed record ChunkDetail(
    Guid ChunkId,
    Guid DocumentId,
    string ExternalKey,
    string Locale,
    string Title,
    string HeadingPathJson,
    string MetadataJson,
    int Ordinal,
    string Content);

public sealed class ChunkDetailReader
{
    private readonly NpgsqlDataSourceFactory _connections;
    private readonly SchemaTables _tables;

    public ChunkDetailReader(NpgsqlDataSourceFactory connections, SchemaTables tables)
    {
        _connections = connections;
        _tables = tables;
    }

    public async Task<Dictionary<Guid, ChunkDetail>> LoadAsync(
        IReadOnlyList<Guid> chunkIds, CancellationToken ct)
    {
        Dictionary<Guid, ChunkDetail> details = [];

        if (chunkIds.Count == 0)
        {
            return details;
        }

        await using NpgsqlConnection connection = await _connections.OpenReadAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            SELECT c.id, c.document_id, d.external_key, d.locale, d.title,
                   c.heading_path::text, d.metadata::text, c.ordinal, c.content
            FROM {_tables.Chunks} c
            JOIN {_tables.Documents} d ON d.id = c.document_id
            WHERE c.id = ANY(@ids)
            """);
        command.Parameters.AddWithValue("ids", chunkIds.ToArray());

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            details[reader.GetGuid(0)] = new ChunkDetail(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetInt32(7), reader.GetString(8));
        }

        return details;
    }
}
