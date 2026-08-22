namespace Neadocs.Engine.Infrastructure.Storage;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Infrastructure.Configuration;
using Npgsql;

public sealed class NpgsqlDataSourceFactory : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;

    public NpgsqlDataSourceFactory(
        IOptions<DocumentEngineOptions> options,
        ILoggerFactory loggerFactory)
        : this(options.Value, loggerFactory)
    {
    }

    public NpgsqlDataSourceFactory(DocumentEngineOptions options, ILoggerFactory? loggerFactory = null)
    {
        if (string.IsNullOrWhiteSpace(options.PostgresConnectionString))
        {
            throw new InvalidOperationException(
                "DocumentEngine:PostgresConnectionString must be set.");
        }

        NpgsqlConnectionStringBuilder connectionString = new(options.PostgresConnectionString);

        if (string.IsNullOrWhiteSpace(connectionString.SearchPath))
        {
            connectionString.SearchPath = $"{options.Schema}, public";
        }

        NpgsqlDataSourceBuilder builder = new(connectionString.ConnectionString);

        if (loggerFactory is not null)
        {
            builder.UseLoggerFactory(loggerFactory);
        }

        _dataSource = builder.Build();
        _commandTimeoutSeconds = options.DatabaseCommandTimeoutSeconds;
    }

    public NpgsqlDataSource DataSource => _dataSource;

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        return connection;
    }

    public NpgsqlCommand CreateCommand(NpgsqlConnection connection, string sql)
    {
        NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _commandTimeoutSeconds;

        return command;
    }

    public NpgsqlCommand CreateCommand(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        NpgsqlCommand command = CreateCommand(connection, sql);
        command.Transaction = transaction;

        return command;
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await OpenAsync(cancellationToken);
            await using NpgsqlCommand command = CreateCommand(connection, "SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
