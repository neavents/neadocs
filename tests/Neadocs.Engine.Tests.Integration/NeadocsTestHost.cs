namespace Neadocs.Engine.Tests.Integration;

using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

public sealed class NeadocsTestHost : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SchemaPrefix = "neadocs_test_";

    public const string ProductionSchema = "neadocs";

    public const string AdminKey = "admin-key";

    public const string WriterKey = "writer-key";

    public const string ReaderKey = "reader-key";

    public const string OtherTenantKey = "other-tenant-key";

    public const string Tenant = "tenant-a";

    public const string OtherTenant = "tenant-b";

    public NeadocsTestHost()
    {
        Schema = TestSchema.Name(SchemaPrefix);
        ConnectionString = ResolveConnectionString();

        if (Schema == ProductionSchema || !Schema.StartsWith(SchemaPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to run: the suite's schema resolved to '{Schema}'. It must be a "
                + $"throwaway schema starting with '{SchemaPrefix}'.");
        }

        // Each host declares its own embedding state rather than trusting another fixture's
        // teardown. These are process-global, so leaving them to disposal order made the vector
        // and lexical suites flip behaviour depending on which ran first.
        Environment.SetEnvironmentVariable("DocumentEngine__EmbeddingModels__0__Provider", null);
        Environment.SetEnvironmentVariable("DocumentEngine__EmbeddingModels__0__Model", null);
        Environment.SetEnvironmentVariable("DocumentEngine__EmbeddingModels__0__Dimensions", null);

        Environment.SetEnvironmentVariable("DocumentEngine__PostgresConnectionString", ConnectionString);
        Environment.SetEnvironmentVariable("DocumentEngine__Schema", Schema);
        Environment.SetEnvironmentVariable(
            "DocumentEngine__AllowedProjectKeys",
            $"{Tenant}:{AdminKey}:admin,"
            + $"{Tenant}:{WriterKey}:write,"
            + $"{Tenant}:{ReaderKey}:read,"
            + $"{OtherTenant}:{OtherTenantKey}:admin");
        Environment.SetEnvironmentVariable("DocumentEngine__JwtSymmetricKey", JwtKey);
        Environment.SetEnvironmentVariable("DocumentEngine__Text__Locales__0", "tr");
        Environment.SetEnvironmentVariable("DocumentEngine__Text__Locales__1", "en");
        Environment.SetEnvironmentVariable("DocumentEngine__Text__Locales__2", "fr");
        Environment.SetEnvironmentVariable("DocumentEngine__Text__Locales__3", "ar");
        Environment.SetEnvironmentVariable("DocumentEngine__Text__DefaultLocale", "tr");
        Environment.SetEnvironmentVariable("DocumentEngine__Text__LocaleFallback__tr__0", "en");
        Environment.SetEnvironmentVariable("DocumentEngine__EnablePrometheusScrape", "true");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Npgsql", "Warning");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Microsoft.AspNetCore", "Warning");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
    }

    public static string JwtKey => new('t', 48);

    public string Schema { get; }

    public string ConnectionString { get; }

    public async Task InitializeAsync()
    {
        // Before anything else: clear out schemas that earlier runs left behind. Disposal already
        // drops this run's own, and thirty-three had still accumulated — a run that is cancelled,
        // times out or crashes never reaches its teardown, so the cleanup has to happen on the way
        // in as well as on the way out. See TestSchema for what the debris cost.
        await TestSchema.SweepStaleAsync(ConnectionString);

        using HttpClient warmUp = CreateClient();
    }

    public HttpClient AnonymousClient() => CreateClient();

    public HttpClient ClientWithKey(string projectKey)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Project-Key", projectKey);

        return client;
    }

    public async Task<NpgsqlConnection> OpenAsync()
    {
        NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();

        return connection;
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;

        object? result = await command.ExecuteScalarAsync();

        return result is null or DBNull ? default : (T)result;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DropSchemaAsync();
        await base.DisposeAsync();
    }

    public async Task DropSchemaAsync()
    {
        if (!Schema.StartsWith(SchemaPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to drop schema '{Schema}': it is not a throwaway test schema.");
        }

        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {Schema} CASCADE";
        await command.ExecuteNonQueryAsync();
    }

    private static string ResolveConnectionString() =>
        Environment.GetEnvironmentVariable("NEADOCS_TEST_POSTGRES")
        ?? "Host=127.0.0.1;Port=5432;Database=neavents;Username=neavents;Password=neavents_dev";
}

[CollectionDefinition(Name)]
public sealed class NeadocsCollection : ICollectionFixture<NeadocsTestHost>
{
    public const string Name = "neadocs";
}
