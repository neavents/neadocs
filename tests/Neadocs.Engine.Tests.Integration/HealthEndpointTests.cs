namespace Neadocs.Engine.Tests.Integration;

using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

/// <summary>
/// The paths Kubernetes probes have to be the paths this service answers.
/// </summary>
/// <remarks>
/// <para>
/// This service served liveness at <c>/health</c> and readiness at <c>/ready</c>, while its
/// generated manifest probed <c>/health/live</c> and <c>/health/ready</c> — the estate convention
/// every other service follows. Both were internally consistent and neither was wrong on its own,
/// so nothing looked broken: the startup probe would simply have 404'd thirty times and the pod
/// been killed, forever, the first time this was deployed.
/// </para>
/// <para>
/// The old paths still answer, because things already point at them. What this test pins is that
/// the probed ones do too.
/// </para>
/// </remarks>
[Collection(NeadocsCollection.Name)]
public sealed class HealthEndpointTests
{
    private readonly NeadocsTestHost _host;

    public HealthEndpointTests(NeadocsTestHost host) => _host = host;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health")]
    public async Task Liveness_answers_without_authentication(string path)
    {
        // Anonymous on purpose: the kubelet does not carry a tenant key, and a liveness probe that
        // 401s restarts a perfectly healthy pod.
        using HttpClient client = _host.AnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/ready")]
    public async Task Readiness_answers_without_authentication(string path)
    {
        using HttpClient client = _host.AnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(path);

        // OK once the schema is migrated, 503 while it is not — both mean the endpoint exists and
        // is answering the question. A 404 is the failure this test is here for.
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable,
            $"{path} answered {(int)response.StatusCode}; a probe path must never 404");
    }
}
