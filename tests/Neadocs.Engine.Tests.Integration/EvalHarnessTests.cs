namespace Neadocs.Engine.Tests.Integration;

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;

[Collection(NeadocsCollection.Name)]
public sealed class EvalHarnessTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NeadocsTestHost _host;
    private string _collection = string.Empty;

    public EvalHarnessTests(NeadocsTestHost host) => _host = host;

    private HttpClient Admin() => _host.ClientWithKey(NeadocsTestHost.AdminKey);

    public async Task InitializeAsync()
    {
        _collection = "eval-" + Guid.NewGuid().ToString("N")[..8];

        using HttpClient client = Admin();
        await client.PutAsJsonAsync($"/api/v1/collections/{_collection}", new { name = "Eval" }, Json);

        await SeedAsync(client, "publishing-a-menu", "Menüyü yayınlama",
            "# Menüyü yayınlama\n\n## Adımlar\n\nMenüyü yayınlamak için düzenle ekranına gidin.");
        await SeedAsync(client, "password-reset", "Şifremi unuttum",
            "# Şifremi unuttum\n\n## Sıfırlama\n\nŞifrenizi sıfırlamak için bağlantıyı kullanın.");
        await SeedAsync(client, "qr-troubleshooting", "Karekod çalışmıyor",
            "# Karekod çalışmıyor\n\n## Kontrol\n\nKarekod okunmuyorsa etiketi değiştirin.");
        await SeedAsync(client, "adding-tables", "Masa ekleme",
            "# Masa ekleme\n\n## Adımlar\n\nYeni masa eklemek için masalar ekranını açın.");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedAsync(HttpClient client, string key, string title, string content)
    {
        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/v1/collections/{_collection}/documents/{key}",
            new { locale = "tr", title, content },
            Json);

        response.EnsureSuccessStatusCode();
    }

    private object GoldenSet() => new
    {
        collection = _collection,
        locale = "tr",
        mode = "lexical",
        cases = new object[]
        {
            new { query = "menüyü yayınlama", expect = new[] { "publishing-a-menu" }, maxRank = 3 },
            new { query = "menuyu yayinlama", expect = new[] { "publishing-a-menu" }, maxRank = 3 },
            new { query = "karekod calismiyor", expect = new[] { "qr-troubleshooting" }, maxRank = 3 },
            new { query = "sifremi unuttum", expect = new[] { "password-reset" }, maxRank = 1 },
            new { query = "masa ekleme", expect = new[] { "adding-tables" }, maxRank = 3 },
        },
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> RunAsync(object set)
    {
        using HttpClient client = Admin();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/eval/run", set, Json);

        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(Json));
    }

    [Fact]
    public async Task TheGoldenSetPasses()
    {
        (HttpStatusCode status, JsonElement report) = await RunAsync(GoldenSet());

        status.Should().Be(HttpStatusCode.OK);
        report.GetProperty("meets").GetBoolean().Should().BeTrue();
        report.GetProperty("passed").GetInt32().Should().Be(5);
        report.GetProperty("failures").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ReportsRecallAndMrr()
    {
        (_, JsonElement report) = await RunAsync(GoldenSet());

        report.GetProperty("recallAt1").GetDouble().Should().BeGreaterThan(0);
        report.GetProperty("recallAt3").GetDouble().Should().Be(1.0);
        report.GetProperty("recallAt10").GetDouble().Should().Be(1.0);
        report.GetProperty("mrr").GetDouble().Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1);
        report.GetProperty("meanLatencyMs").GetDouble().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ReportsPerCaseRanks()
    {
        (_, JsonElement report) = await RunAsync(GoldenSet());

        foreach (JsonElement testCase in report.GetProperty("cases").EnumerateArray())
        {
            testCase.GetProperty("actualRank").GetInt32().Should().BeGreaterThan(0);
            testCase.GetProperty("passed").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public async Task FailsWithA422WhenACaseDoesNotHold()
    {
        (HttpStatusCode status, JsonElement report) = await RunAsync(new
        {
            collection = _collection,
            locale = "tr",
            mode = "lexical",
            cases = new object[]
            {
                new { query = "sifremi unuttum", expect = new[] { "password-reset" }, maxRank = 1 },
                new { query = "tamamen alakasiz", expect = new[] { "publishing-a-menu" }, maxRank = 3 },
            },
        });

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        report.GetProperty("meets").GetBoolean().Should().BeFalse();
        report.GetProperty("failures").GetArrayLength().Should().Be(1);
        report.GetProperty("failures")[0].GetString().Should().Contain("tamamen alakasiz");
    }

    [Fact]
    public async Task AMaxRankOneCaseFailingSinksTheWholeRunEvenIfRecallIsHigh()
    {
        (HttpStatusCode status, JsonElement report) = await RunAsync(new
        {
            collection = _collection,
            locale = "tr",
            mode = "lexical",
            cases = new object[]
            {
                new { query = "menuyu yayinlama", expect = new[] { "publishing-a-menu" }, maxRank = 3 },
                new { query = "karekod", expect = new[] { "qr-troubleshooting" }, maxRank = 3 },
                new { query = "masa", expect = new[] { "adding-tables" }, maxRank = 3 },
                new { query = "yayinlama", expect = new[] { "password-reset" }, maxRank = 1 },
            },
        });

        report.GetProperty("recallAt3").GetDouble().Should().BeGreaterThanOrEqualTo(0.75);
        report.GetProperty("meets").GetBoolean().Should().BeFalse(
            "a maxRank 1 case is a hard requirement regardless of aggregate recall");
        status.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task TheUndiacriticisedCaseIsWhatCatchesAFoldingRegression()
    {
        (_, JsonElement report) = await RunAsync(new
        {
            collection = _collection,
            locale = "tr",
            mode = "lexical",
            cases = new object[]
            {
                new { query = "sifremi unuttum", expect = new[] { "password-reset" }, maxRank = 1 },
                new { query = "menuyu yayinlama", expect = new[] { "publishing-a-menu" }, maxRank = 3 },
                new { query = "karekod calismiyor", expect = new[] { "qr-troubleshooting" }, maxRank = 3 },
            },
        });

        report.GetProperty("meets").GetBoolean().Should().BeTrue(
            "every one of these is typed the way an owner types, without diacritics");
    }

    [Fact]
    public async Task RunningAgainstAnUnknownCollectionIs404()
    {
        using HttpClient client = Admin();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/eval/run", new
        {
            collection = "no-such-collection",
            locale = "tr",
            cases = new object[] { new { query = "x", expect = new[] { "y" }, maxRank = 3 } },
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RejectsAnEmptyCaseList()
    {
        using HttpClient client = Admin();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/eval/run",
            new { collection = _collection, locale = "tr", cases = Array.Empty<object>() }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RequiresAdminScope()
    {
        using HttpClient reader = _host.ClientWithKey(NeadocsTestHost.ReaderKey);

        HttpResponseMessage response = await reader.PostAsJsonAsync("/api/v1/eval/run", GoldenSet(), Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProviderHealthReportsNoModelInLexicalOnlyMode()
    {
        using HttpClient client = Admin();

        JsonElement health = await client.GetFromJsonAsync<JsonElement>("/api/v1/health/providers", Json);

        health.GetProperty("configured").GetBoolean().Should().BeFalse();
        health.GetProperty("providers").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ReindexReturnsAJobIdAndRunsToCompletion()
    {
        using HttpClient client = Admin();

        HttpResponseMessage response = await client.PostAsync(
            $"/api/v1/collections/{_collection}/reindex", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        JsonElement accepted = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        string jobId = accepted.GetProperty("jobId").GetString()!;
        accepted.GetProperty("state").GetString().Should().Be("queued");

        JsonElement job = default;

        for (int attempt = 0; attempt < 60; attempt++)
        {
            job = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{jobId}", Json);

            if (job.GetProperty("state").GetString() is "succeeded" or "failed")
            {
                break;
            }

            await Task.Delay(250);
        }

        job.GetProperty("state").GetString().Should().Be("succeeded");
        job.GetProperty("kind").GetString().Should().Be("reindex");
        job.GetProperty("processed").GetInt32().Should().Be(4);
        job.GetProperty("total").GetInt32().Should().Be(4);
        job.GetProperty("errors").GetArrayLength().Should().Be(0);

        (HttpStatusCode status, JsonElement report) = await RunAsync(GoldenSet());

        status.Should().Be(HttpStatusCode.OK);
        report.GetProperty("meets").GetBoolean().Should().BeTrue(
            "a reindex must not change what search finds");
    }

    [Fact]
    public async Task AnUnknownJobIs404()
    {
        using HttpClient client = Admin();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OneTenantCannotReadAnothersJob()
    {
        using HttpClient owner = Admin();
        using HttpClient other = _host.ClientWithKey(NeadocsTestHost.OtherTenantKey);

        HttpResponseMessage queued = await owner.PostAsync(
            $"/api/v1/collections/{_collection}/reindex", null);
        string jobId = (await queued.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("jobId").GetString()!;

        (await other.GetAsync($"/api/v1/jobs/{jobId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.GetAsync($"/api/v1/jobs/{jobId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReindexingAnUnknownCollectionIs404()
    {
        using HttpClient client = Admin();

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/collections/no-such-collection/reindex", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
