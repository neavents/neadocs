namespace Neadocs.Engine.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

public sealed class VectorTestHost : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminKey = "vec-admin-key";
    public const string Tenant = "vec-tenant";
    public const string ModelSlug = "probe_model";

    public VectorTestHost()
    {
        Schema = "neadocs_vec_" + Guid.NewGuid().ToString("N")[..10];
        ConnectionString = Environment.GetEnvironmentVariable("NEADOCS_TEST_POSTGRES")
            ?? "Host=127.0.0.1;Port=5432;Database=neavents;Username=neavents;Password=neavents_dev";

        Environment.SetEnvironmentVariable("DocumentEngine__PostgresConnectionString", ConnectionString);
        Environment.SetEnvironmentVariable("DocumentEngine__Schema", Schema);
        Environment.SetEnvironmentVariable("DocumentEngine__AllowedProjectKeys", $"{Tenant}:{AdminKey}:admin");
        Environment.SetEnvironmentVariable("DocumentEngine__JwtSymmetricKey", new string('v', 40));
        Environment.SetEnvironmentVariable("DocumentEngine__Text__Locales__0", "tr");
        Environment.SetEnvironmentVariable("DocumentEngine__Text__Locales__1", "en");
        Environment.SetEnvironmentVariable("DocumentEngine__Text__DefaultLocale", "tr");
        Environment.SetEnvironmentVariable("DocumentEngine__Text__LocaleFallback__tr__0", "en");
        Environment.SetEnvironmentVariable("DocumentEngine__EmbeddingModels__0__Provider", "deterministic");
        Environment.SetEnvironmentVariable("DocumentEngine__EmbeddingModels__0__Model", "probe-model");
        Environment.SetEnvironmentVariable("DocumentEngine__EmbeddingModels__0__Dimensions", "128");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Npgsql", "Warning");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Microsoft.AspNetCore", "Warning");
    }

    public string Schema { get; }

    public string ConnectionString { get; }

    public HttpClient Admin()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Project-Key", AdminKey);

        return client;
    }

    public Task InitializeAsync()
    {
        using HttpClient warmUp = CreateClient();

        return Task.CompletedTask;
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;

        object? result = await command.ExecuteScalarAsync();

        return result is null or DBNull ? default : (T)result;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (Schema.StartsWith("neadocs_vec_", StringComparison.Ordinal))
        {
            await using NpgsqlConnection connection = new(ConnectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS {Schema} CASCADE";
            await command.ExecuteNonQueryAsync();
        }

        Environment.SetEnvironmentVariable("DocumentEngine__EmbeddingModels__0__Provider", null);
        Environment.SetEnvironmentVariable("DocumentEngine__EmbeddingModels__0__Model", null);
        Environment.SetEnvironmentVariable("DocumentEngine__EmbeddingModels__0__Dimensions", null);

        await base.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class VectorCollection : ICollectionFixture<VectorTestHost>
{
    public const string Name = "neadocs-vector";
}

[Collection(VectorCollection.Name)]
public sealed class VectorSearchTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly VectorTestHost _host;
    private string _collection = string.Empty;

    public VectorSearchTests(VectorTestHost host) => _host = host;

    public async Task InitializeAsync()
    {
        _collection = "vec-" + Guid.NewGuid().ToString("N")[..8];

        using HttpClient client = _host.Admin();
        await client.PutAsJsonAsync($"/api/v1/collections/{_collection}", new { name = "Vec" }, Json);

        await SeedAsync(client, "publishing", "tr", "Menüyü yayınlama",
            "# Menüyü yayınlama\n\n## Adımlar\n\nMenüyü yayınlamak için düzenle ekranına gidin.");
        await SeedAsync(client, "password", "tr", "Şifremi unuttum",
            "# Şifremi unuttum\n\n## Sıfırlama\n\nŞifrenizi sıfırlamak için bağlantıyı kullanın.");
        await SeedAsync(client, "qr", "tr", "Karekod",
            "# Karekod\n\n## Kontrol\n\nKarekod okunmuyorsa etiketi değiştirin.");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedAsync(HttpClient client, string key, string locale, string title, string content)
    {
        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/v1/collections/{_collection}/documents/{key}",
            new { locale, title, content },
            Json);

        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> SearchAsync(object request)
    {
        using HttpClient client = _host.Admin();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/collections/{_collection}/search", request, Json);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private static List<string> KeysOf(JsonElement result) =>
        [.. result.GetProperty("hits").EnumerateArray()
            .Select(h => h.GetProperty("externalKey").GetString()!)];

    [Fact]
    public async Task PgvectorIsInstalledAndTheEmbeddingTableExists()
    {
        (await _host.ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector')"))
            .Should().BeTrue();

        (await _host.ScalarAsync<bool>(
            $"SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = '{_host.Schema}' "
            + $"AND tablename = 'chunk_embeddings__{VectorTestHost.ModelSlug}')"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task TheHnswIndexIsBuiltWithTheDeclaredParameters()
    {
        string? definition = await _host.ScalarAsync<string>(
            $"SELECT indexdef FROM pg_indexes WHERE schemaname = '{_host.Schema}' "
            + $"AND indexname = 'ix_emb_{VectorTestHost.ModelSlug}_hnsw'");

        definition.Should().NotBeNull();
        definition.Should().Contain("hnsw").And.Contain("vector_cosine_ops")
            .And.Contain("m='16'").And.Contain("ef_construction='64'");
    }

    [Fact]
    public async Task IngestionWritesAVectorForEveryChunk()
    {
        long chunks = await _host.ScalarAsync<long>(
            $"SELECT count(*) FROM {_host.Schema}.chunks");
        long vectors = await _host.ScalarAsync<long>(
            $"SELECT count(*) FROM {_host.Schema}.chunk_embeddings__{VectorTestHost.ModelSlug}");

        vectors.Should().Be(chunks).And.BeGreaterThan(0);
    }

    [Fact]
    public async Task TheVectorColumnCarriesTheDeclaredWidth()
    {
        int width = await _host.ScalarAsync<int>(
            $"""
            SELECT a.atttypmod FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'embedding'
            WHERE n.nspname = '{_host.Schema}'
              AND c.relname = 'chunk_embeddings__{VectorTestHost.ModelSlug}'
            """);

        width.Should().Be(128);
    }

    [Fact]
    public async Task VectorModeReturnsResults()
    {
        JsonElement result = await SearchAsync(new { query = "yayinlama", locale = "tr", mode = "vector" });

        result.GetProperty("mode").GetString().Should().Be("vector");
        result.GetProperty("degraded").GetBoolean().Should().BeFalse();
        result.GetProperty("hits").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HybridIsNotDegradedWhenAModelIsConfigured()
    {
        JsonElement result = await SearchAsync(new { query = "yayinlama", locale = "tr", mode = "hybrid" });

        result.GetProperty("degraded").GetBoolean().Should().BeFalse();
        result.GetProperty("mode").GetString().Should().Be("hybrid");
    }

    [Fact]
    public async Task HybridFindsTheSameDocumentLexicalSearchDoes()
    {
        JsonElement hybrid = await SearchAsync(new { query = "sifremi unuttum", locale = "tr", mode = "hybrid" });

        KeysOf(hybrid).Should().Contain("password");
    }

    [Fact]
    public async Task ExplainReportsBothRanksInHybridMode()
    {
        JsonElement result = await SearchAsync(new
        {
            query = "sifremi unuttum",
            locale = "tr",
            mode = "hybrid",
            explain = true,
        });

        JsonElement explain = result.GetProperty("hits")[0].GetProperty("explain");

        (explain.TryGetProperty("lexicalRank", out _) || explain.TryGetProperty("vectorRank", out _))
            .Should().BeTrue();
    }

    [Fact]
    public async Task HybridScoresAreReciprocalRankFusionValues()
    {
        JsonElement result = await SearchAsync(new { query = "sifremi", locale = "tr", mode = "hybrid" });

        double top = result.GetProperty("hits")[0].GetProperty("score").GetDouble();

        top.Should().BeGreaterThan(0).And.BeLessThan(1,
            "an RRF score is a sum of 1/(k+rank) terms, never a raw ts_rank or cosine value");
    }

    [Fact]
    public async Task HybridIsDeterministicAcrossIdenticalCalls()
    {
        JsonElement first = await SearchAsync(new { query = "karekod", locale = "tr", mode = "hybrid" });
        JsonElement second = await SearchAsync(new { query = "karekod", locale = "tr", mode = "hybrid" });

        KeysOf(first).Should().Equal(KeysOf(second));
    }

    [Fact]
    public async Task AMetadataFilterStillAppliesInVectorMode()
    {
        using HttpClient client = _host.Admin();

        await client.PutAsJsonAsync($"/api/v1/collections/{_collection}/documents/tagged",
            new
            {
                locale = "tr",
                title = "Etiketli",
                content = "# Etiketli\n\nBenzersizetiketiceriktir.",
                metadata = new { section = "special" },
            },
            Json);

        JsonElement filtered = await SearchAsync(new
        {
            query = "benzersizetiketiceriktir",
            locale = "tr",
            mode = "vector",
            filter = new { section = "special" },
        });

        KeysOf(filtered).Should().Contain("tagged");

        JsonElement excluded = await SearchAsync(new
        {
            query = "benzersizetiketiceriktir",
            locale = "tr",
            mode = "vector",
            filter = new { section = "does-not-exist" },
        });

        KeysOf(excluded).Should().NotContain("tagged");
    }

    [Fact]
    public async Task AnUnchangedReUpsertReusesCachedVectors()
    {
        using HttpClient client = _host.Admin();

        long cachedBefore = await _host.ScalarAsync<long>(
            $"SELECT count(*) FROM {_host.Schema}.embedding_cache");

        await SeedAsync(client, "publishing", "tr", "Menüyü yayınlama",
            "# Menüyü yayınlama\n\n## Adımlar\n\nMenüyü yayınlamak için düzenle ekranına gidin.");

        long cachedAfter = await _host.ScalarAsync<long>(
            $"SELECT count(*) FROM {_host.Schema}.embedding_cache");

        cachedAfter.Should().Be(cachedBefore, "an unchanged document must not re-embed anything");
    }

    [Fact]
    public async Task TheEmbeddingCacheIsPopulated()
    {
        long cached = await _host.ScalarAsync<long>(
            $"SELECT count(*) FROM {_host.Schema}.embedding_cache WHERE model_slug = '{VectorTestHost.ModelSlug}'");

        cached.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task NothingIsQueuedInTheBacklogWhenTheProviderWorks()
    {
        long backlog = await _host.ScalarAsync<long>(
            $"SELECT count(*) FROM {_host.Schema}.embedding_backlog");

        backlog.Should().Be(0);
    }

    [Fact]
    public async Task DeletingADocumentCascadesToItsVectors()
    {
        using HttpClient client = _host.Admin();

        await SeedAsync(client, "doomed", "tr", "Silinecek", "# Silinecek\n\nGovde.");

        long before = await _host.ScalarAsync<long>(
            $"SELECT count(*) FROM {_host.Schema}.chunk_embeddings__{VectorTestHost.ModelSlug}");

        await client.DeleteAsync($"/api/v1/collections/{_collection}");

        long after = await _host.ScalarAsync<long>(
            $"SELECT count(*) FROM {_host.Schema}.chunk_embeddings__{VectorTestHost.ModelSlug}");

        after.Should().BeLessThan(before,
            "the embedding table has an ON DELETE CASCADE to chunks");
    }
}
