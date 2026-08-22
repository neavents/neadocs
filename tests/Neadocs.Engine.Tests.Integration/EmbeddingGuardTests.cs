namespace Neadocs.Engine.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Providers;
using Neadocs.Engine.Infrastructure.Storage;
using Npgsql;

public sealed class ThrowingEmbeddingProvider : IEmbeddingProvider
{
    private readonly Func<bool> _shouldFail;

    public ThrowingEmbeddingProvider(string model, int dimensions, Func<bool> shouldFail)
    {
        Model = model;
        Dimensions = dimensions;
        _shouldFail = shouldFail;
    }

    public string Name => "flaky";

    public string Model { get; }

    public int Dimensions { get; }

    public int MaxBatch => 64;

    public int MaxConcurrentRequests => 1;

    public int Calls { get; private set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        Calls++;

        if (_shouldFail())
        {
            throw new EmbeddingRequestFailedException("the vendor is down");
        }

        List<float[]> vectors = [];

        foreach (string text in texts)
        {
            vectors.Add(DeterministicEmbeddingProvider.Embed(text, Dimensions));
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    public Task<bool> IsHealthyAsync(CancellationToken ct) => Task.FromResult(!_shouldFail());
}

public sealed class EmbeddingGuardTests : IAsyncLifetime
{
    private const string ConnectionFallback =
        "Host=127.0.0.1;Port=5432;Database=neavents;Username=neavents;Password=neavents_dev";

    private readonly string _schema = "neadocs_guard_" + Guid.NewGuid().ToString("N")[..10];
    private readonly List<NpgsqlDataSourceFactory> _factories = [];

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("NEADOCS_TEST_POSTGRES") ?? ConnectionFallback;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (NpgsqlDataSourceFactory factory in _factories)
        {
            await factory.DisposeAsync();
        }

        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {_schema} CASCADE";
        await command.ExecuteNonQueryAsync();
    }

    private DocumentEngineOptions Options(params EmbeddingModelOptions[] models)
    {
        DocumentEngineOptions options = new()
        {
            PostgresConnectionString = ConnectionString,
            Schema = _schema,
            AllowedProjectKeys = "t:k",
            Text = new TextOptions { Locales = ["en"], DefaultLocale = "en" },
        };

        options.EmbeddingModels.AddRange(models);

        return options;
    }

    private PostgresSchemaMigrator Migrator(DocumentEngineOptions options)
    {
        NpgsqlDataSourceFactory factory = new(options);
        _factories.Add(factory);

        return new PostgresSchemaMigrator(
            factory,
            new SchemaTables(options),
            Microsoft.Extensions.Options.Options.Create(options),
            new EmbeddingModelRegistry(options),
            new VectorTypeInfo(),
            new MigrationState(),
            NullLogger<PostgresSchemaMigrator>.Instance);
    }

    private static EmbeddingModelOptions Model(int dimensions, bool retired = false) => new()
    {
        Provider = "deterministic",
        Model = "guard-model",
        Dimensions = dimensions,
        Retired = retired,
    };

    private async Task<long> ScalarAsync(string sql)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;

        object? result = await command.ExecuteScalarAsync();

        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    [Fact]
    public async Task ADimensionMismatchRefusesTheBootAndNamesBothNumbers()
    {
        await Migrator(Options(Model(128))).MigrateAsync(CancellationToken.None);

        Func<Task> act = () => Migrator(Options(Model(256))).MigrateAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
                .Contain("128").And.Contain("256")
                .And.Contain("chunk_embeddings__guard_model")
                .And.Contain("reindex");
    }

    [Fact]
    public async Task TheSameDimensionMigratesAgainWithoutComplaint()
    {
        await Migrator(Options(Model(64))).MigrateAsync(CancellationToken.None);

        Func<Task> act = () => Migrator(Options(Model(64))).MigrateAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AnEmptyOrphanedEmbeddingTableIsDropped()
    {
        await Migrator(Options(Model(32))).MigrateAsync(CancellationToken.None);

        (await ScalarAsync(
            $"SELECT count(*) FROM pg_tables WHERE schemaname='{_schema}' "
            + "AND tablename='chunk_embeddings__guard_model'")).Should().Be(1);

        await Migrator(Options()).MigrateAsync(CancellationToken.None);

        (await ScalarAsync(
            $"SELECT count(*) FROM pg_tables WHERE schemaname='{_schema}' "
            + "AND tablename='chunk_embeddings__guard_model'")).Should().Be(0);
    }

    [Fact]
    public async Task AnOrphanedEmbeddingTableHoldingRowsRefusesTheBoot()
    {
        DocumentEngineOptions options = Options(Model(8));
        await Migrator(options).MigrateAsync(CancellationToken.None);

        await SeedOneVectorAsync();

        Func<Task> act = () => Migrator(Options()).MigrateAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
                .Contain("holds 1 row")
                .And.Contain("Retired")
                .And.Contain("silently destroy");
    }

    [Fact]
    public async Task DeclaringTheModelRetiredPermitsTheBootAndKeepsTheData()
    {
        await Migrator(Options(Model(8))).MigrateAsync(CancellationToken.None);
        await SeedOneVectorAsync();

        Func<Task> act = () =>
            Migrator(Options(Model(8, retired: true))).MigrateAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();

        (await ScalarAsync($"SELECT count(*) FROM {_schema}.chunk_embeddings__guard_model"))
            .Should().Be(1, "retirement stops writing without destroying anything");
    }

    [Fact]
    public async Task AProviderOutageLeavesTheChunkQueuedAndTheDocumentStillStored()
    {
        DocumentEngineOptions options = Options(Model(16));
        await Migrator(options).MigrateAsync(CancellationToken.None);

        Guid chunkId = await SeedChunkAsync();

        bool failing = true;
        ThrowingEmbeddingProvider provider = new("guard-model", 16, () => failing);

        EmbeddingStore store = await BuildStoreAsync(options, provider);

        await store.EmbedAsync([new PendingEmbedding(chunkId, "hash-1", "some body text")], CancellationToken.None);

        (await ScalarAsync($"SELECT count(*) FROM {_schema}.embedding_backlog"))
            .Should().Be(1, "a vendor outage must never lose the chunk");

        (await ScalarAsync($"SELECT count(*) FROM {_schema}.chunk_embeddings__guard_model"))
            .Should().Be(0);

        (await ScalarAsync($"SELECT count(*) FROM {_schema}.chunks"))
            .Should().Be(1, "the document and its chunks are committed regardless");
    }

    [Fact]
    public async Task TheBacklogDrainsOnceTheProviderRecovers()
    {
        DocumentEngineOptions options = Options(Model(16));
        options.BacklogWorker.MaxAttempts = 10;
        await Migrator(options).MigrateAsync(CancellationToken.None);

        Guid chunkId = await SeedChunkAsync();

        bool failing = true;
        ThrowingEmbeddingProvider provider = new("guard-model", 16, () => failing);
        EmbeddingStore store = await BuildStoreAsync(options, provider);

        await store.EmbedAsync([new PendingEmbedding(chunkId, "hash-1", "some body text")], CancellationToken.None);
        (await ScalarAsync($"SELECT count(*) FROM {_schema}.embedding_backlog")).Should().Be(1);

        failing = false;

        EmbeddingBacklogWorker worker = new(
            store,
            new EmbeddingModelRegistry(options),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<EmbeddingBacklogWorker>.Instance);

        await worker.DrainAsync(CancellationToken.None);

        (await ScalarAsync($"SELECT count(*) FROM {_schema}.embedding_backlog"))
            .Should().Be(0, "recovery must clear the queue");

        (await ScalarAsync($"SELECT count(*) FROM {_schema}.chunk_embeddings__guard_model"))
            .Should().Be(1, "and the vector must actually be written");
    }

    [Fact]
    public async Task RetryingIncrementsTheAttemptCountAndRecordsTheError()
    {
        DocumentEngineOptions options = Options(Model(16));
        await Migrator(options).MigrateAsync(CancellationToken.None);

        Guid chunkId = await SeedChunkAsync();
        ThrowingEmbeddingProvider provider = new("guard-model", 16, () => true);
        EmbeddingStore store = await BuildStoreAsync(options, provider);

        PendingEmbedding pending = new(chunkId, "hash-1", "some body text");

        await store.EmbedAsync([pending], CancellationToken.None);
        await store.EmbedAsync([pending], CancellationToken.None);

        (await ScalarAsync($"SELECT attempts FROM {_schema}.embedding_backlog")).Should().Be(2);

        (await ScalarAsync(
            $"SELECT count(*) FROM {_schema}.embedding_backlog WHERE last_error LIKE '%vendor is down%'"))
            .Should().Be(1, "the failure has to be visible, not merely counted");
    }

    [Fact]
    public async Task TheBootProbeRefusesAModelReturningTheWrongWidth()
    {
        DocumentEngineOptions options = Options(Model(16));

        EmbeddingChain chain = new(
            options,
            new EmbeddingModelRegistry(options),
            NullLogger<EmbeddingChain>.Instance,
            model => new ThrowingEmbeddingProvider(model.Model, 999, () => false));

        Func<Task> act = () => chain.ProbeDimensionsAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
                .Contain("999").And.Contain("16").And.Contain("Refusing");
    }

    [Fact]
    public async Task TheBootProbeRefusesAProviderThatCannotAnswerAtAll()
    {
        DocumentEngineOptions options = Options(Model(16));

        EmbeddingChain chain = new(
            options,
            new EmbeddingModelRegistry(options),
            NullLogger<EmbeddingChain>.Instance,
            model => new ThrowingEmbeddingProvider(model.Model, 16, () => true));

        Func<Task> act = () => chain.ProbeDimensionsAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("dimension probe").And.Contain("Refusing to start");
    }

    private async Task<EmbeddingStore> BuildStoreAsync(DocumentEngineOptions options, IEmbeddingProvider provider)
    {
        NpgsqlDataSourceFactory factory = new(options);
        _factories.Add(factory);

        EmbeddingModelRegistry registry = new(options);

        EmbeddingChain chain = new(
            options, registry, NullLogger<EmbeddingChain>.Instance, _ => provider);

        VectorTypeInfo vectorType = new();
        string vectorSchema = await VectorSchemaAsync();
        vectorType.Resolve(vectorSchema);
        vectorType.ResolveTrigram(vectorSchema);

        return new EmbeddingStore(
            factory, new SchemaTables(options), chain, registry, vectorType,
            NullLogger<EmbeddingStore>.Instance);
    }

    private async Task SeedOneVectorAsync()
    {
        Guid chunkId = await SeedChunkAsync();

        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        string vectorSchema = await VectorSchemaAsync();

        command.CommandText =
            $"INSERT INTO {_schema}.chunk_embeddings__guard_model (chunk_id, embedding) "
            + $"VALUES ('{chunkId}', '[1,0,0,0,0,0,0,0]'::{vectorSchema}.vector)";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> VectorSchemaAsync()
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT n.nspname FROM pg_extension e "
            + "JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'vector'";

        return (string?)await command.ExecuteScalarAsync() ?? "public";
    }

    private async Task<Guid> SeedChunkAsync()
    {
        Guid collectionId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        Guid chunkId = Guid.NewGuid();

        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {_schema}.collections (id, tenant_id, key, name)
            VALUES ('{collectionId}', 't', 'c-{collectionId:N}', 'c')
            ON CONFLICT DO NOTHING;
            INSERT INTO {_schema}.documents
                (id, collection_id, external_key, locale, title, content_hash, current_revision)
            VALUES ('{documentId}', '{collectionId}', 'k-{documentId:N}', 'en', 't', 'h', 1);
            INSERT INTO {_schema}.chunks
                (id, document_id, revision, ordinal, content, content_hash, token_count,
                 tsv_folded, normalizer_tag, normalizer_hash)
            VALUES ('{chunkId}', '{documentId}', 1, 0, 'some body text', 'hash-1', 3,
                    to_tsvector('simple', 'some body text'), 'en', 'nh');
            """;
        await command.ExecuteNonQueryAsync();

        return chunkId;
    }
}
