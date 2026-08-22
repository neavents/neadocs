namespace Neadocs.Engine.Tests.Integration;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Neadocs.Engine.Infrastructure.Storage;

[Collection(NeadocsCollection.Name)]
public sealed class MigratorTests
{
    private static readonly string[] ExpectedTables =
    [
        "chunks",
        "collections",
        "document_revisions",
        "documents",
        "embedding_backlog",
        "embedding_cache",
        "jobs",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "ix_backlog_due",
        "ix_chunks_doc",
        "ix_chunks_hash",
        "ix_chunks_norm",
        "ix_chunks_trgm",
        "ix_chunks_tsv",
        "ix_documents_collection_live",
        "ix_documents_external_key",
        "ix_documents_metadata",
        "ix_jobs_tenant",
    ];

    private readonly NeadocsTestHost _host;

    public MigratorTests(NeadocsTestHost host) => _host = host;

    [Fact]
    public async Task CreatesEveryDeclaredTable()
    {
        List<string> tables = await TableNamesAsync();

        tables.Should().Contain(ExpectedTables);
    }

    [Fact]
    public async Task CreatesNoTableThatIsNotDeclared()
    {
        List<string> tables = await TableNamesAsync();

        tables.Should().BeSubsetOf(ExpectedTables);
    }

    [Fact]
    public async Task CreatesEveryDeclaredIndex()
    {
        List<string> indexes = await IndexNamesAsync();

        indexes.Should().Contain(ExpectedIndexes);
    }

    [Fact]
    public async Task InstallsTheTrigramExtensionSomewhereReachable()
    {
        bool present = await _host.ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm')");

        present.Should().BeTrue();
    }

    [Fact]
    public async Task ExtensionsAreNeverCreatedInsideTheEngineSchema()
    {
        long captured = await _host.ScalarAsync<long>(
            $"""
            SELECT count(*) FROM pg_extension e
            JOIN pg_namespace n ON n.oid = e.extnamespace
            WHERE n.nspname = '{_host.Schema}'
            """);

        captured.Should().Be(0,
            "an extension is a database-global object. Creating one inside this suite's throwaway "
            + "schema captures it in something designed to be dropped, and dropping it orphans "
            + "every index that referenced its access method — leaving schemas that DROP CASCADE "
            + "can no longer remove.");
    }

    [Fact]
    public async Task ZeroProviderModeCreatesNoEmbeddingTables()
    {
        long embeddingTables = await _host.ScalarAsync<long>(
            $"SELECT count(*) FROM pg_tables WHERE schemaname = '{_host.Schema}' "
            + "AND tablename LIKE 'chunk_embeddings__%'");

        embeddingTables.Should().Be(0,
            "with no model configured the engine must run lexical-only and build nothing that "
            + "would require pgvector. Whether the extension exists database-wide is not this "
            + "schema's business — another service or suite may well have installed it.");
    }

    [Fact]
    public async Task RunningTheMigratorAgainIsANoOp()
    {
        List<string> before = await TableNamesAsync();
        long collectionsBefore = await _host.ScalarAsync<long>(
            $"SELECT count(*) FROM {_host.Schema}.collections");

        using IServiceScope scope = _host.Services.CreateScope();
        PostgresSchemaMigrator migrator =
            scope.ServiceProvider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .OfType<PostgresSchemaMigrator>()
                .Single();

        await migrator.MigrateAsync(CancellationToken.None);

        List<string> after = await TableNamesAsync();

        after.Should().BeEquivalentTo(before);
        (await _host.ScalarAsync<long>($"SELECT count(*) FROM {_host.Schema}.collections"))
            .Should().Be(collectionsBefore);
    }

    [Fact]
    public async Task ConcurrentMigratorsDoNotRaceEachOther()
    {
        using IServiceScope scope = _host.Services.CreateScope();
        PostgresSchemaMigrator migrator =
            scope.ServiceProvider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .OfType<PostgresSchemaMigrator>()
                .Single();

        Task[] concurrent =
        [
            migrator.MigrateAsync(CancellationToken.None),
            migrator.MigrateAsync(CancellationToken.None),
            migrator.MigrateAsync(CancellationToken.None),
            migrator.MigrateAsync(CancellationToken.None),
        ];

        Func<Task> act = () => Task.WhenAll(concurrent);

        await act.Should().NotThrowAsync(
            "the migrator takes a Postgres advisory lock precisely so that four containers "
            + "starting at once cannot deadlock or duplicate DDL");

        (await TableNamesAsync()).Should().Contain(ExpectedTables);
    }

    [Fact]
    public async Task ChunksCarryTheColumnsRetrievalDependsOn()
    {
        List<string> columns = await ColumnNamesAsync("chunks");

        columns.Should().Contain(
        [
            "id", "document_id", "revision", "ordinal", "heading_path", "content",
            "content_hash", "token_count", "tsv_folded", "normalizer_tag", "normalizer_hash",
        ]);
    }

    [Fact]
    public async Task HeadingPathIsJsonbSoNoReadingDirectionIsBakedIn()
    {
        string? type = await _host.ScalarAsync<string>(
            $"""
            SELECT data_type FROM information_schema.columns
            WHERE table_schema = '{_host.Schema}'
              AND table_name = 'chunks' AND column_name = 'heading_path'
            """);

        type.Should().Be("jsonb");
    }

    [Fact]
    public async Task TheFoldedVectorColumnIsATsvector()
    {
        string? type = await _host.ScalarAsync<string>(
            $"""
            SELECT udt_name FROM information_schema.columns
            WHERE table_schema = '{_host.Schema}'
              AND table_name = 'chunks' AND column_name = 'tsv_folded'
            """);

        type.Should().Be("tsvector");
    }

    [Fact]
    public async Task DocumentsCarryTranslationProvenance()
    {
        List<string> columns = await ColumnNamesAsync("documents");

        columns.Should().Contain(["source_locale", "source_content_hash", "deleted_at"]);
    }

    [Fact]
    public async Task DeletingACollectionCascadesToItsDocumentsAndChunks()
    {
        string? action = await _host.ScalarAsync<string>(
            $"""
            SELECT rc.delete_rule
            FROM information_schema.referential_constraints rc
            JOIN information_schema.table_constraints tc
              ON tc.constraint_name = rc.constraint_name
             AND tc.constraint_schema = rc.constraint_schema
            WHERE tc.table_schema = '{_host.Schema}'
              AND tc.table_name = 'documents'
            LIMIT 1
            """);

        action.Should().Be("CASCADE");
    }

    [Fact]
    public async Task TheNaturalKeyIsUniquePerCollectionKeyAndLocale()
    {
        bool exists = await _host.ScalarAsync<bool>(
            $"""
            SELECT EXISTS (
                SELECT 1 FROM pg_indexes
                WHERE schemaname = '{_host.Schema}'
                  AND tablename = 'documents'
                  AND indexdef LIKE '%UNIQUE%'
                  AND indexdef LIKE '%collection_id%'
                  AND indexdef LIKE '%external_key%'
                  AND indexdef LIKE '%locale%')
            """);

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task TheLiveDocumentIndexIsPartial()
    {
        string? definition = await _host.ScalarAsync<string>(
            $"""
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = '{_host.Schema}' AND indexname = 'ix_documents_collection_live'
            """);

        definition.Should().Contain("WHERE (deleted_at IS NULL)");
    }

    private async Task<List<string>> TableNamesAsync()
    {
        await using Npgsql.NpgsqlConnection connection = await _host.OpenAsync();
        await using Npgsql.NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT tablename FROM pg_tables WHERE schemaname = '{_host.Schema}' ORDER BY tablename";

        List<string> names = [];
        await using Npgsql.NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<List<string>> IndexNamesAsync()
    {
        await using Npgsql.NpgsqlConnection connection = await _host.OpenAsync();
        await using Npgsql.NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT indexname FROM pg_indexes WHERE schemaname = '{_host.Schema}' ORDER BY indexname";

        List<string> names = [];
        await using Npgsql.NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<List<string>> ColumnNamesAsync(string table)
    {
        await using Npgsql.NpgsqlConnection connection = await _host.OpenAsync();
        await using Npgsql.NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = '{_host.Schema}' AND table_name = '{table}'
            ORDER BY column_name
            """;

        List<string> names = [];
        await using Npgsql.NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
