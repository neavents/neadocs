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
    /// <summary>A standby further behind than this is not read from.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(1);

    /// <summary>How often the lag is re-measured. Cheap, but not free, and it need not be exact.</summary>
    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(30);

    private readonly NpgsqlDataSource _dataSource;
    private readonly NpgsqlDataSource _readDataSource;
    private readonly string _readConnectionString;
    private readonly int _commandTimeoutSeconds;

    private long _lastLagTicks = -1;
    private long _lastSampleAtTicks;
    private int _sampling;

    /// <summary>True when reads and writes reach different servers.</summary>
    public bool HasSeparateReadServer { get; }

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

        // The read data source gets the same search_path treatment as the write one. Every table
        // reference in this engine is already schema-qualified through SchemaTables, so setting it
        // is belt-and-braces — but a read connection that silently disagreed with the write
        // connection about which schema it is looking at would be a genuinely nasty bug to find.
        // Same rule on both sides.
        string read = options.PostgresReadConnectionString;

        if (string.IsNullOrWhiteSpace(read))
        {
            _readConnectionString = connectionString.ConnectionString;
            _readDataSource = _dataSource;
            HasSeparateReadServer = false;

            return;
        }

        NpgsqlConnectionStringBuilder readConnectionString = new(read);

        if (string.IsNullOrWhiteSpace(readConnectionString.SearchPath))
        {
            readConnectionString.SearchPath = $"{options.Schema}, public";
        }

        // Hosts, not whole strings. The read string differs from the write one in pool size and
        // application name by design, so comparing them verbatim would report a separate server
        // where there is none — and then route reads to the primary while claiming otherwise.
        HasSeparateReadServer = !string.Equals(
            readConnectionString.Host,
            connectionString.Host,
            StringComparison.OrdinalIgnoreCase);

        _readConnectionString = readConnectionString.ConnectionString;

        if (!HasSeparateReadServer)
        {
            _readDataSource = _dataSource;

            return;
        }

        NpgsqlDataSourceBuilder readBuilder = new(_readConnectionString);

        if (loggerFactory is not null)
        {
            readBuilder.UseLoggerFactory(loggerFactory);
        }

        _readDataSource = readBuilder.Build();
    }

    public NpgsqlDataSource DataSource => _dataSource;

    /// <summary>Where a read would go right now, for logging and tests.</summary>
    public bool WouldUseReplica()
    {
        if (!HasSeparateReadServer)
        {
            return false;
        }

        long ticks = Interlocked.Read(ref _lastLagTicks);

        // -1 means "not measured, or the measurement failed". Both send the read to the primary:
        // an unknown standby is not a usable standby, and defaulting the other way would serve
        // arbitrarily stale documents the first time the probe could not run.
        return ticks >= 0 && ticks <= StaleAfter.Ticks;
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        return connection;
    }

    /// <summary>
    /// A connection for a read-only query: the standby when it is keeping up, the primary when it
    /// is not, and always the primary when no separate read server is configured.
    /// </summary>
    /// <remarks>
    /// Every caller of this must be a pure read. A write sent here does not corrupt anything —
    /// Postgres refuses it on a standby — but it fails at runtime rather than at review, which is
    /// why the one write in <c>DocumentReader</c> (<c>SoftDeleteAsync</c>) stays on
    /// <see cref="OpenAsync"/>.
    /// </remarks>
    public async Task<NpgsqlConnection> OpenReadAsync(CancellationToken cancellationToken)
    {
        MaybeSample();

        NpgsqlDataSource source = WouldUseReplica() ? _readDataSource : _dataSource;

        return await source.OpenConnectionAsync(cancellationToken);
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

    /// <summary>
    /// How far behind the standby is, in milliseconds, or null when it cannot be measured —
    /// including when the read connection turns out to reach a primary.
    /// </summary>
    /// <remarks>
    /// The LSN equality test comes first and carries the weight.
    /// <c>now() - pg_last_xact_replay_timestamp()</c> on its own is time since the last WRITE, not
    /// lag: on a quiet database it climbs without bound while the standby is perfectly current.
    /// The naive form measured 2433ms against a standby that had replayed everything.
    /// </remarks>
    public async Task<double?> ReplayLagMillisecondsAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = new(_readConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = LagSql;

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull ? null : Convert.ToDouble(value);
    }

    private const string LagSql =
        "SELECT CASE " +
        "WHEN NOT pg_is_in_recovery() THEN NULL " +
        "WHEN pg_last_wal_receive_lsn() = pg_last_wal_replay_lsn() THEN 0 " +
        "ELSE EXTRACT(EPOCH FROM (now() - pg_last_xact_replay_timestamp())) * 1000 END";

    private void RecordLag(double? milliseconds) =>
        Interlocked.Exchange(
            ref _lastLagTicks,
            milliseconds is { } ms && ms >= 0 ? (long)(ms * TimeSpan.TicksPerMillisecond) : -1);

    /// <summary>
    /// Re-measures the lag at most once every <see cref="SampleEvery"/>, off the request path.
    /// </summary>
    /// <remarks>
    /// Self-feeding on purpose. An earlier version of this pattern elsewhere in the estate relied on
    /// a hosted service to push samples in, and in the one service that never registered that hosted
    /// service the replica sat unused at zero percent while looking perfectly configured.
    /// </remarks>
    private void MaybeSample()
    {
        if (!HasSeparateReadServer)
        {
            return;
        }

        long now = DateTimeOffset.UtcNow.UtcTicks;

        if (now - Interlocked.Read(ref _lastSampleAtTicks) <= SampleEvery.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastSampleAtTicks, now);

        // Fire and forget: the read in flight uses the previous reading, and a standby whose lag
        // crossed the threshold between two samples thirty seconds apart is not one worth racing.
        _ = Task.Run(async () =>
        {
            try
            {
                RecordLag(await ReplayLagMillisecondsAsync());
            }
            catch (Exception)
            {
                RecordLag(null);
            }
            finally
            {
                Interlocked.Exchange(ref _sampling, 0);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        // Reference equality, not a flag: when no separate read server is configured the two fields
        // are the SAME data source, and disposing it twice would throw on the second.
        if (!ReferenceEquals(_readDataSource, _dataSource))
        {
            await _readDataSource.DisposeAsync();
        }

        await _dataSource.DisposeAsync();
    }
}
