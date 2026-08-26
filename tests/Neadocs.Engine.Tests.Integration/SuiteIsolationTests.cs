namespace Neadocs.Engine.Tests.Integration;

using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

[Collection(NeadocsCollection.Name)]
public sealed class SuiteIsolationTests
{
    private readonly NeadocsTestHost _host;

    public SuiteIsolationTests(NeadocsTestHost host) => _host = host;

    [Fact]
    public void TheSuiteNeverRunsAgainstTheProductionSchema()
    {
        _host.Schema.Should().NotBe(NeadocsTestHost.ProductionSchema);
        _host.Schema.Should().StartWith(NeadocsTestHost.SchemaPrefix);
    }

    [Fact]
    public async Task TheSuiteCreatedItsOwnSchema()
    {
        bool exists = await _host.ScalarAsync<bool>(
            $"SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = '{_host.Schema}')");

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task TheSuiteNeverWritesToTheDeploymentsRealSchema()
    {
        bool productionExists = await _host.ScalarAsync<bool>(
            $"SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = '{NeadocsTestHost.ProductionSchema}')");

        if (!productionExists)
        {
            return;
        }

        string sql = "SELECT count(*) FROM " + NeadocsTestHost.ProductionSchema + ".collections "
            + $"WHERE tenant_id IN ('{NeadocsTestHost.Tenant}', '{NeadocsTestHost.OtherTenant}')";

        long leaked = await _host.ScalarAsync<long>(sql);

        leaked.Should().Be(0,
            "a real deployment owns that schema; the suite runs in a throwaway one and must never "
            + "leave a row behind in the deployment's");
    }

    [Fact]
    public async Task EveryTableTheMigratorCreatedLivesInANeadocsOwnedSchema()
    {
        long outside = await _host.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_tables
            WHERE tablename IN (
                'collections','documents','document_revisions','chunks',
                'embedding_cache','embedding_backlog','jobs')
              AND schemaname NOT LIKE 'neadocs%'
            """);

        outside.Should().Be(0,
            "no engine table may exist outside a neadocs-owned schema; sibling neadocs_* schemas "
            + "are other concurrent test hosts, which is fine");
    }

    /// <summary>
    /// The premise of the test above: this suite really is sharing a database with other services.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Skipped rather than failed when the sibling schemas are absent, because their absence is not
    /// a defect — it means the suite is running against a database of its own, and there is then
    /// nothing to be isolated from. CI does exactly that: a fresh <c>pgvector/pgvector:pg17</c>
    /// service container with nothing in it but this suite's own schemas.
    /// </para>
    /// <para>
    /// It used to be a plain assertion, and it failed every CI run for that reason — reporting
    /// "expected neighbours to be greater than 0", which reads as a broken engine and means the
    /// database is clean. The check is worth keeping for LOCAL runs, where the suite does share the
    /// estate's Postgres and the isolation above is load-bearing; it is worth nothing in CI.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task OtherServicesSchemasAreUntouched()
    {
        long neighbours = await _host.ScalarAsync<long>(
            "SELECT count(*) FROM pg_namespace WHERE nspname IN ('identity','menu','media','messaging')");

        Skip.If(neighbours == 0,
            "no sibling service schemas: this run has a database to itself, so there is nothing for "
            + "the schema isolation to protect.");

        neighbours.Should().BeGreaterThan(0,
            "the suite shares a database with the rest of the estate, which is exactly why the "
            + "schema isolation above has to hold");
    }
}
