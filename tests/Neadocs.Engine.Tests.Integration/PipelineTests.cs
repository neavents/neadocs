namespace Neadocs.Engine.Tests.Integration;

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System;
using System.Threading.Tasks;
using FluentAssertions;

[Collection(NeadocsCollection.Name)]
public sealed class PipelineTests
{
    private readonly NeadocsTestHost _host;

    public PipelineTests(NeadocsTestHost host) => _host = host;

    [Fact]
    public async Task HealthAnswersWithoutACredential()
    {
        using HttpClient client = _host.AnonymousClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"status\":\"ok\"");
    }

    [Fact]
    public async Task ReadyAnswersOnlyOnceTheMigrationCompletedAndPostgresAnswers()
    {
        using HttpClient client = _host.AnonymousClient();

        HttpResponseMessage response = await client.GetAsync("/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"status\":\"ready\"");
    }

    [Fact]
    public async Task MetricsAreServedWithoutACredential()
    {
        using HttpClient client = _host.AnonymousClient();

        HttpResponseMessage response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnUnknownRouteWithNoCredentialIsRejectedBeforeRouting()
    {
        using HttpClient client = _host.AnonymousClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/collections");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task AnUnauthorizedResponseIsRfc7807AndCarriesTheCorrelationId()
    {
        using HttpClient client = _host.AnonymousClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/anything");
        string body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"type\":").And.Contain("\"title\":")
            .And.Contain("\"status\":401").And.Contain("\"correlationId\":");
    }

    [Fact]
    public async Task AnInboundCorrelationIdIsEchoedBack()
    {
        using HttpClient client = _host.AnonymousClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/health");
        request.Headers.Add("X-Correlation-Id", "req-integration-42");

        HttpResponseMessage response = await client.SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").Should().ContainSingle()
            .Which.Should().Be("req-integration-42");
    }

    [Fact]
    public async Task ACorrelationIdIsGeneratedWhenTheCallerSendsNone()
    {
        using HttpClient client = _host.AnonymousClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        response.Headers.GetValues("X-Correlation-Id").Should().ContainSingle()
            .Which.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AForgedCorrelationIdIsReplacedRatherThanEchoed()
    {
        using HttpClient client = _host.AnonymousClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/health");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "bad value; with junk");

        HttpResponseMessage response = await client.SendAsync(request);

        string echoed = string.Join("", response.Headers.GetValues("X-Correlation-Id"));

        echoed.Should().NotContain(" ").And.NotContain(";");
    }

    [Fact]
    public async Task SecurityHeadersAreSetOnEveryResponse()
    {
        using HttpClient client = _host.AnonymousClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Contain("strict-origin-when-cross-origin");
    }

    [Fact]
    public async Task TheServerHeaderIsNotAdvertised()
    {
        using HttpClient client = _host.AnonymousClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        response.Headers.Contains("Server").Should().BeFalse();
    }

    [Fact]
    public async Task AValidProjectKeyPassesTheCredentialGate()
    {
        using HttpClient client = _host.ClientWithKey(NeadocsTestHost.AdminKey);

        HttpResponseMessage response = await client.GetAsync("/api/v1/collections");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnUnknownProjectKeyIsRejected()
    {
        using HttpClient client = _host.ClientWithKey("not-a-real-key");

        HttpResponseMessage response = await client.GetAsync("/api/v1/collections");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ATenantIdSuppliedAsInputIsIgnoredRatherThanHonoured()
    {
        using HttpClient owner = _host.ClientWithKey(NeadocsTestHost.AdminKey);
        using HttpClient other = _host.ClientWithKey(NeadocsTestHost.OtherTenantKey);

        string key = "tenancy-" + Guid.NewGuid().ToString("N")[..8];
        await owner.PutAsJsonAsync($"/api/v1/collections/{key}", new { name = "Owned" });

        HttpResponseMessage viaQuery = await other.GetAsync(
            $"/api/v1/collections?tenant={NeadocsTestHost.Tenant}");
        HttpResponseMessage viaHeader = await SendWithHeaderAsync(
            other, "X-Tenant-Id", NeadocsTestHost.Tenant);

        foreach (HttpResponseMessage response in new[] { viaQuery, viaHeader })
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().NotContain(key,
                "the tenant comes from the credential and from nowhere else, so naming another "
                + "tenant in a query string or header changes nothing");
        }
    }

    [Fact]
    public async Task OneTenantCannotReachAnothersCollectionByRoute()
    {
        using HttpClient owner = _host.ClientWithKey(NeadocsTestHost.AdminKey);
        using HttpClient other = _host.ClientWithKey(NeadocsTestHost.OtherTenantKey);

        string key = "private-" + Guid.NewGuid().ToString("N")[..8];
        await owner.PutAsJsonAsync($"/api/v1/collections/{key}", new { name = "Owned" });
        await owner.PutAsJsonAsync($"/api/v1/collections/{key}/documents/secret",
            new { locale = "en", title = "Secret", content = "# Secret\n\nConfidential body." });

        (await other.GetAsync($"/api/v1/collections/{key}/documents/secret"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        HttpResponseMessage search = await other.PostAsJsonAsync(
            $"/api/v1/collections/{key}/search", new { query = "confidential" });
        search.StatusCode.Should().Be(HttpStatusCode.NotFound);

        HttpResponseMessage write = await other.PutAsJsonAsync(
            $"/api/v1/collections/{key}/documents/injected",
            new { locale = "en", title = "X", content = "# X\n\nBody." });
        write.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await owner.GetAsync($"/api/v1/collections/{key}/documents/secret"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AReadOnlyKeyCannotWrite()
    {
        using HttpClient reader = _host.ClientWithKey(NeadocsTestHost.ReaderKey);

        HttpResponseMessage response = await reader.PutAsJsonAsync(
            "/api/v1/collections/anything/documents/a",
            new { locale = "en", title = "T", content = "body" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("docs:write");
    }

    [Fact]
    public async Task AWriteKeyCannotAdministerCollections()
    {
        using HttpClient writer = _host.ClientWithKey(NeadocsTestHost.WriterKey);

        HttpResponseMessage response = await writer.PutAsJsonAsync(
            "/api/v1/collections/nope", new { name = "X" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<HttpResponseMessage> SendWithHeaderAsync(
        HttpClient client,
        string header,
        string value)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/collections");
        request.Headers.TryAddWithoutValidation(header, value);

        return await client.SendAsync(request);
    }
}
