namespace Neadocs.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Neadocs.Engine.Features;
using Neadocs.Engine.Infrastructure.Serialization;
using Npgsql;
using NpgsqlTypes;

public static class JobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public sealed class JobStore
{
    private readonly NpgsqlDataSourceFactory _connections;
    private readonly SchemaTables _tables;

    public JobStore(NpgsqlDataSourceFactory connections, SchemaTables tables)
    {
        _connections = connections;
        _tables = tables;
    }

    public async Task<Guid> CreateAsync(string tenant, string kind, CancellationToken ct)
    {
        Guid id = Guid.NewGuid();

        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(connection,
            $"INSERT INTO {_tables.Jobs} (id, tenant_id, kind, state) VALUES (@id, @tenant, @kind, @state)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("state", JobStates.Queued);

        await command.ExecuteNonQueryAsync(ct);

        return id;
    }

    public async Task StartAsync(Guid id, int total, CancellationToken ct) =>
        await UpdateAsync(id, JobStates.Running, processed: 0, total: total, errors: null, ct);

    public async Task ProgressAsync(Guid id, int processed, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(connection,
            $"UPDATE {_tables.Jobs} SET processed = @processed, updated_at = now() WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("processed", processed);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task FinishAsync(Guid id, int processed, IReadOnlyList<string> errors, CancellationToken ct) =>
        await UpdateAsync(
            id,
            errors.Count == 0 ? JobStates.Succeeded : JobStates.Failed,
            processed,
            total: null,
            errors,
            ct);

    private async Task UpdateAsync(
        Guid id, string state, int processed, int? total, IReadOnlyList<string>? errors, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(connection, $"""
            UPDATE {_tables.Jobs}
            SET state = @state,
                processed = @processed,
                total = COALESCE(@total, total),
                errors = COALESCE(@errors::jsonb, errors),
                updated_at = now()
            WHERE id = @id
            """);

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("processed", processed);
        command.Parameters.AddWithValue("total", total is null ? DBNull.Value : total.Value);
        command.Parameters.AddWithValue(
            "errors", NpgsqlDbType.Text, errors is null ? DBNull.Value : Serialize(errors));

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<JobResponse?> GetAsync(string tenant, Guid id, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _connections.OpenAsync(ct);
        await using NpgsqlCommand command = _connections.CreateCommand(connection,
            $"SELECT id, kind, state, processed, total, errors::text, created_at, updated_at "
            + $"FROM {_tables.Jobs} WHERE id = @id AND tenant_id = @tenant");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenant);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new JobResponse
        {
            Id = reader.GetGuid(0),
            Kind = reader.GetString(1),
            State = reader.GetString(2),
            Processed = reader.GetInt32(3),
            Total = reader.GetInt32(4),
            Errors = Deserialize(reader.GetString(5)),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(7),
        };
    }

    private static string Serialize(IReadOnlyList<string> errors)
    {
        StringBuilder builder = new();
        builder.Append('[');

        for (int i = 0; i < errors.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(JsonSerializer.Serialize(errors[i], NeadocsJsonContext.Default.String));
        }

        builder.Append(']');

        return builder.ToString();
    }

    private static List<string> Deserialize(string json)
    {
        List<string> errors = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                errors.Add(element.GetString() ?? string.Empty);
            }
        }
        catch (JsonException)
        {
            return errors;
        }

        return errors;
    }
}
