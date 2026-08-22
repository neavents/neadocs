namespace Neadocs.Engine.Tests.Integration;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Providers;
using Neadocs.Engine.Infrastructure.Storage;
using Npgsql;
using Xunit;

/// <summary>
/// A job abandoned by a dead process must not stay "running" forever.
/// </summary>
/// <remarks>
/// <para>
/// A job row is advanced by the process running it, so a process that is killed rather than
/// stopped — OOM, a node eviction, SIGKILL after the grace period — leaves its row saying
/// <c>running</c> with nothing anywhere that would ever move it. It is terminal in practice: a
/// caller polling that id waits forever, and a reindex that died looks exactly like one still
/// working.
/// </para>
/// <para>
/// Graceful shutdown is handled where the work runs, which marks its own job failed. This covers
/// the ungraceful case, and it has to be blunt: with more than one replica there is no way to tell
/// a job abandoned by a dead pod from one another pod is actively running, so only rows that have
/// gone quiet for a long time are touched.
/// </para>
/// </remarks>
public sealed class StaleJobRecoveryTests : IAsyncLifetime
{
    private const string ConnectionFallback =
        "Host=127.0.0.1;Port=5432;Database=neavents;Username=neavents;Password=neavents_dev";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("NEADOCS_TEST_POSTGRES") ?? ConnectionFallback;

    private readonly string _schema = "neadocs_jobs_" + Guid.NewGuid().ToString("N")[..10];
    private readonly List<NpgsqlDataSourceFactory> _factories = [];

    private DocumentEngineOptions _options = null!;
    private JobStore _jobs = null!;

    public async Task InitializeAsync()
    {
        _options = new DocumentEngineOptions
        {
            PostgresConnectionString = ConnectionString,
            Schema = _schema,
            AllowedProjectKeys = "t:k",
            Text = new TextOptions { Locales = ["en"], DefaultLocale = "en" },
        };

        NpgsqlDataSourceFactory factory = new(_options);
        _factories.Add(factory);

        await new PostgresSchemaMigrator(
            factory,
            new SchemaTables(_options),
            Microsoft.Extensions.Options.Options.Create(_options),
            new EmbeddingModelRegistry(_options),
            new VectorTypeInfo(),
            new MigrationState(),
            NullLogger<PostgresSchemaMigrator>.Instance).MigrateAsync(CancellationToken.None);

        _jobs = new JobStore(factory, new SchemaTables(_options));
    }

    public async Task DisposeAsync()
    {
        await using (NpgsqlConnection connection = new(ConnectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand drop = connection.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS {_schema} CASCADE";
            await drop.ExecuteNonQueryAsync();
        }

        foreach (NpgsqlDataSourceFactory factory in _factories)
        {
            await factory.DisposeAsync();
        }
    }

    private async Task AgeJobAsync(Guid id, TimeSpan by)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {_schema}.jobs SET updated_at = now() - make_interval(secs => {by.TotalSeconds}) WHERE id = '{id}'";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> StateOfAsync(Guid id)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT state FROM {_schema}.jobs WHERE id = '{id}'";

        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_job_abandoned_long_ago_is_failed()
    {
        Guid id = await _jobs.CreateAsync("t", "reindex", CancellationToken.None);
        await _jobs.StartAsync(id, 100, CancellationToken.None);
        await AgeJobAsync(id, TimeSpan.FromHours(6));

        int recovered = await _jobs.FailStaleAsync(TimeSpan.FromHours(2), CancellationToken.None);

        recovered.Should().Be(1);
        (await StateOfAsync(id)).Should().Be(JobStates.Failed,
            "a row nothing will ever advance must not keep claiming to be running");
    }

    [Fact]
    public async Task A_job_that_is_still_reporting_progress_is_left_alone()
    {
        // The whole risk of a sweep like this is killing live work. Progress is written every 25
        // documents, so an active job keeps its row fresh and must be untouchable.
        Guid id = await _jobs.CreateAsync("t", "reindex", CancellationToken.None);
        await _jobs.StartAsync(id, 100, CancellationToken.None);
        await AgeJobAsync(id, TimeSpan.FromMinutes(5));

        int recovered = await _jobs.FailStaleAsync(TimeSpan.FromHours(2), CancellationToken.None);

        recovered.Should().Be(0);
        (await StateOfAsync(id)).Should().Be(JobStates.Running);
    }

    [Fact]
    public async Task A_finished_job_is_never_reopened()
    {
        Guid id = await _jobs.CreateAsync("t", "reindex", CancellationToken.None);
        await _jobs.StartAsync(id, 1, CancellationToken.None);
        await _jobs.FinishAsync(id, 1, [], CancellationToken.None);
        await AgeJobAsync(id, TimeSpan.FromDays(30));

        await _jobs.FailStaleAsync(TimeSpan.FromHours(2), CancellationToken.None);

        (await StateOfAsync(id)).Should().Be(JobStates.Succeeded,
            "a completed job is a record, and rewriting it would erase what actually happened");
    }

    [Fact]
    public async Task A_queued_job_nobody_ever_picked_up_is_also_failed()
    {
        // Queued and never started is the same dead end: the process that would have run it is
        // gone, and nothing polls the table looking for orphans.
        Guid id = await _jobs.CreateAsync("t", "reindex", CancellationToken.None);
        await AgeJobAsync(id, TimeSpan.FromHours(6));

        await _jobs.FailStaleAsync(TimeSpan.FromHours(2), CancellationToken.None);

        (await StateOfAsync(id)).Should().Be(JobStates.Failed);
    }

    [Fact]
    public async Task The_reason_is_recorded_so_an_operator_knows_what_to_do()
    {
        Guid id = await _jobs.CreateAsync("t", "reindex", CancellationToken.None);
        await _jobs.StartAsync(id, 100, CancellationToken.None);
        await AgeJobAsync(id, TimeSpan.FromHours(6));

        await _jobs.FailStaleAsync(TimeSpan.FromHours(2), CancellationToken.None);

        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT errors::text FROM {_schema}.jobs WHERE id = '{id}'";
        string errors = (string)(await command.ExecuteScalarAsync())!;

        errors.Should().Contain("Run it again",
            "a failed job with no reason tells an operator nothing they can act on");
    }
}
