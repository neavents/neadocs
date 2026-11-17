namespace Neadocs.Engine.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;

[Collection(NeadocsCollection.Name)]
public sealed class ParanoidNeadocsIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NeadocsTestHost _host;

    public ParanoidNeadocsIntegrationTests(NeadocsTestHost host) => _host = host;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..16];

    private static string MintJwt(
        string key,
        string? tenant,
        string[]? scopes = null,
        DateTime? expiresUtc = null)
    {
        List<Claim> claims = [];
        if (tenant is not null)
        {
            claims.Add(new Claim("tenant", tenant));
        }

        foreach (string scope in scopes ?? ["read"])
        {
            claims.Add(new Claim("scope", scope));
        }

        DateTime now = DateTime.UtcNow;
        DateTime exp = expiresUtc ?? now.AddMinutes(10);
        SymmetricSecurityKey signingKey = new(Encoding.UTF8.GetBytes(key));
        JwtSecurityToken token = new(
            claims: claims,
            notBefore: (expiresUtc ?? now).AddMinutes(-5),
            expires: exp,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ───── Auth smuggling, JWT verification, scopes & cross-tenant isolation ─────

    [Theory]
    [InlineData(NeadocsTestHost.AdminKey, "smuggled")]
    [InlineData("smuggled", NeadocsTestHost.AdminKey)]
    [InlineData(NeadocsTestHost.AdminKey, NeadocsTestHost.AdminKey)]
    [InlineData(NeadocsTestHost.AdminKey, NeadocsTestHost.OtherTenantKey)]
    public async Task DuplicateProjectKeyHeaders_FailClosed401(string first, string second)
    {
        using HttpClient client = _host.AnonymousClient();
        using HttpRequestMessage req = new(HttpMethod.Get, "/api/v1/collections");
        req.Headers.Add("X-Project-Key", new[] { first, second });

        HttpResponseMessage resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task JwtAuth_RespectsTenantScopesExpirySignatureAndMissingTenant()
    {
        using HttpClient client = _host.AnonymousClient();

        string readJwt = MintJwt(NeadocsTestHost.JwtKey, NeadocsTestHost.Tenant, ["docs:read"]);
        using (HttpRequestMessage listReq = new(HttpMethod.Get, "/api/v1/collections"))
        {
            listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", readJwt);
            (await client.SendAsync(listReq)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        string unknownScopeJwt = MintJwt(NeadocsTestHost.JwtKey, NeadocsTestHost.Tenant, ["read"]);
        using (HttpRequestMessage unknownScopeReq = new(HttpMethod.Get, "/api/v1/collections"))
        {
            unknownScopeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", unknownScopeJwt);
            (await client.SendAsync(unknownScopeReq)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (HttpRequestMessage writeReq = new(HttpMethod.Put, $"/api/v1/collections/{Unique("jwtcol")}"))
        {
            writeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", readJwt);
            writeReq.Content = JsonContent.Create(new { name = "JWT" });
            (await client.SendAsync(writeReq)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        string expiredJwt = MintJwt(NeadocsTestHost.JwtKey, NeadocsTestHost.Tenant, ["docs:admin"], DateTime.UtcNow.AddMinutes(-30));
        using (HttpRequestMessage expReq = new(HttpMethod.Get, "/api/v1/collections"))
        {
            expReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expiredJwt);
            (await client.SendAsync(expReq)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        string wrongKeyJwt = MintJwt(new string('x', 48), NeadocsTestHost.Tenant, ["admin"]);
        using (HttpRequestMessage badSigReq = new(HttpMethod.Get, "/api/v1/collections"))
        {
            badSigReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wrongKeyJwt);
            (await client.SendAsync(badSigReq)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        string noTenantJwt = MintJwt(NeadocsTestHost.JwtKey, null, ["admin"]);
        using (HttpRequestMessage noTenantReq = new(HttpMethod.Get, "/api/v1/collections"))
        {
            noTenantReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", noTenantJwt);
            (await client.SendAsync(noTenantReq)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task CrossTenantAndScopeIsolation_AcrossAllEndpoints()
    {
        using HttpClient tenantA = _host.ClientWithKey(NeadocsTestHost.AdminKey);
        using HttpClient tenantB = _host.ClientWithKey(NeadocsTestHost.OtherTenantKey);
        using HttpClient reader = _host.ClientWithKey(NeadocsTestHost.ReaderKey);
        using HttpClient writer = _host.ClientWithKey(NeadocsTestHost.WriterKey);

        string col = Unique("iso");
        (await tenantA.PutAsJsonAsync($"/api/v1/collections/{col}", new { name = "Tenant A" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        await tenantA.PutAsJsonAsync($"/api/v1/collections/{col}/documents/doc-1",
            new { locale = "en", title = "Secret", content = "# Secret\n\nConfidential A." }, Json);

        // Writer cannot perform admin operations
        (await writer.PutAsJsonAsync($"/api/v1/collections/{Unique("w")}", new { name = "W" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await writer.PostAsJsonAsync($"/api/v1/collections/{col}/reindex", new { }, Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await writer.GetAsync("/api/v1/health/providers")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Reader cannot write or delete
        (await reader.PutAsJsonAsync($"/api/v1/collections/{col}/documents/r",
            new { locale = "en", title = "R", content = "x" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.DeleteAsync($"/api/v1/collections/{col}/documents/doc-1"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Tenant B cannot see or modify Tenant A resources
        (await tenantB.GetAsync($"/api/v1/collections/{col}/documents")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await tenantB.GetAsync($"/api/v1/collections/{col}/documents/doc-1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await tenantB.DeleteAsync($"/api/v1/collections/{col}/documents/doc-1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await tenantB.DeleteAsync($"/api/v1/collections/{col}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await tenantB.PostAsJsonAsync($"/api/v1/collections/{col}/search", new { query = "Confidential" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ───── NUL bytes, JSON \u0000, and malformed JSON (must never hit 500) ─────

    [Fact]
    public async Task NulBytesAndJsonNullEscapes_Return400AcrossAllEndpoints_WhileEscapedBackslashSurvives()
    {
        using HttpClient admin = _host.ClientWithKey(NeadocsTestHost.AdminKey);
        string col = Unique("nul");
        (await admin.PutAsJsonAsync($"/api/v1/collections/{col}", new { name = "Nul Test" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Collection NUL in name or \u0000 in config -> 400
        (await admin.PutAsJsonAsync($"/api/v1/collections/{col}", new { name = "Bad\0Name" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using (StringContent badCfg = new("{\"config\":{\"a\":\"x\\u0000y\"}}", Encoding.UTF8, "application/json"))
        {
            (await admin.PutAsync($"/api/v1/collections/{col}", badCfg)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        // Document NUL in title/content/metadata -> 400
        (await admin.PutAsJsonAsync($"/api/v1/collections/{col}/documents/d1",
            new { locale = "en", title = "T\0", content = "Body" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync($"/api/v1/collections/{col}/documents/d1",
            new { locale = "en", title = "T", content = "Body\0" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using (StringContent badMeta = new("{\"locale\":\"en\",\"title\":\"T\",\"content\":\"Body\",\"metadata\":{\"nested\":[\"a\\u0000b\"]}}", Encoding.UTF8, "application/json"))
        {
            (await admin.PutAsync($"/api/v1/collections/{col}/documents/d1", badMeta)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        // Valid double-escaped literal "\\u0000" text is NOT NUL -> must succeed (200)
        using (StringContent okMeta = new("{\"locale\":\"en\",\"title\":\"Literal\",\"content\":\"# Literal\\n\\nText.\",\"metadata\":{\"literal\":\"\\\\u0000\"}}", Encoding.UTF8, "application/json"))
        {
            (await admin.PutAsync($"/api/v1/collections/{col}/documents/literal-ok", okMeta))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Bulk upsert containing NUL must yield 400 item status, NOT 500 PostgresException
        using StringContent bulkPayload = new("""
            {
              "documents": [
                { "externalKey": "good-1", "locale": "en", "title": "Good", "content": "# Good\n\nFine." },
                { "externalKey": "bad-nul", "locale": "en", "title": "Bad", "content": "x\u0000y" }
              ]
            }
            """, Encoding.UTF8, "application/json");
        HttpResponseMessage bulkResp = await admin.PostAsync($"/api/v1/collections/{col}/documents:bulk", bulkPayload);
        bulkResp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement bulkBody = await bulkResp.Content.ReadFromJsonAsync<JsonElement>(Json);
        bulkBody.GetProperty("results")[0].GetProperty("status").GetInt32().Should().Be(200);
        bulkBody.GetProperty("results")[1].GetProperty("status").GetInt32().Should().Be(400);

        // Search NUL in query or \u0000 in filter -> 400
        (await admin.PostAsJsonAsync($"/api/v1/collections/{col}/search", new { query = "bad\0query" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using (StringContent badFilter = new("{\"query\":\"Literal\",\"filter\":{\"literal\":\"a\\u0000b\"}}", Encoding.UTF8, "application/json"))
        {
            (await admin.PostAsync($"/api/v1/collections/{col}/search", badFilter)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task MalformedJsonBodies_Return400Not500()
    {
        using HttpClient admin = _host.ClientWithKey(NeadocsTestHost.AdminKey);
        string col = Unique("mal");

        using StringContent invalidJson = new("{ \"name\": \"broken", Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await admin.PutAsync($"/api/v1/collections/{col}", invalidJson);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ───── tsquery syntax injection & concurrent upsert consistency ─────

    [Theory]
    [InlineData("& | ! ( ) : * ' \\ <->")]
    [InlineData("\"unclosed quote AND OR NOT -- /* */")]
    [InlineData("...,,,---???!!!")]
    [InlineData("İSTANBUL IŞIK ŞİFRE")]
    public async Task LexicalSearch_HostileOperatorsAndTurkishCasing_NeverCrashPostgres(string query)
    {
        using HttpClient admin = _host.ClientWithKey(NeadocsTestHost.AdminKey);
        string col = Unique("tsq");
        await admin.PutAsJsonAsync($"/api/v1/collections/{col}", new { name = "TSQ" }, Json);
        await admin.PutAsJsonAsync($"/api/v1/collections/{col}/documents/tr-doc",
            new { locale = "tr", title = "İstanbul Işık", content = "# İstanbul\n\nIşık şifre bilgileri." }, Json);

        HttpResponseMessage resp = await admin.PostAsJsonAsync(
            $"/api/v1/collections/{col}/search", new { query, locale = "tr" }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConcurrentDocumentUpserts_PreserveConsistentFinalRevision()
    {
        using HttpClient admin = _host.ClientWithKey(NeadocsTestHost.AdminKey);
        string col = Unique("race");
        await admin.PutAsJsonAsync($"/api/v1/collections/{col}", new { name = "Race" }, Json);

        // Seed row first, then fire 6 concurrent updates across distinct content bodies
        HttpResponseMessage seed = await admin.PutAsJsonAsync($"/api/v1/collections/{col}/documents/concurrent-doc",
            new { locale = "en", title = "Seed", content = "# Seed\n\nInitial version." }, Json);
        seed.EnsureSuccessStatusCode();

        Task<HttpResponseMessage>[] tasks = new Task<HttpResponseMessage>[6];
        for (int i = 0; i < tasks.Length; i++)
        {
            int idx = i;
            tasks[i] = admin.PutAsJsonAsync($"/api/v1/collections/{col}/documents/concurrent-doc",
                new { locale = "en", title = $"Rev {idx}", content = $"# Rev {idx}\n\nParagraph content {idx}." }, Json);
        }

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        foreach (HttpResponseMessage r in responses)
        {
            ((int)r.StatusCode).Should().Be(200);
        }

        HttpResponseMessage getResp = await admin.GetAsync($"/api/v1/collections/{col}/documents/concurrent-doc?locale=en");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement doc = await getResp.Content.ReadFromJsonAsync<JsonElement>(Json);
        doc.GetProperty("revision").GetInt32().Should().BeGreaterThanOrEqualTo(2);
    }
}
