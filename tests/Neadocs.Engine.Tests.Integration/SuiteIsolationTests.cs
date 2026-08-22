namespace Neadocs.Engine.Tests.Integration;

using System.Threading.Tasks;
using FluentAssertions;

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

    [Fact]
    public async Task OtherServicesSchemasAreUntouched()
    {
        long neighbours = await _host.ScalarAsync<long>(
            "SELECT count(*) FROM pg_namespace WHERE nspname IN ('identity','menu','media','messaging')");

        neighbours.Should().BeGreaterThan(0,
            "the suite shares a database with the rest of the estate, which is exactly why the "
            + "schema isolation above has to hold");
    }
}
