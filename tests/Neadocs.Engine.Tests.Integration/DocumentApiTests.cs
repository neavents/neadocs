namespace Neadocs.Engine.Tests.Integration;

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;

[Collection(NeadocsCollection.Name)]
public sealed class DocumentApiTests
{
    private readonly NeadocsTestHost _host;

    public DocumentApiTests(NeadocsTestHost host) => _host = host;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Admin() => _host.ClientWithKey(NeadocsTestHost.AdminKey);

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<string> NewCollectionAsync(HttpClient client)
    {
        string key = Unique("col");
        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/v1/collections/{key}", new { name = "Test" }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return key;
    }

    private static object Doc(string locale, string title, string content) =>
        new { locale, title, content };

    [Fact]
    public async Task CreatesACollectionAndListsIt()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        JsonElement list = await client.GetFromJsonAsync<JsonElement>("/api/v1/collections", Json);

        list.GetProperty("items").EnumerateArray()
            .Should().Contain(c => c.GetProperty("key").GetString() == key);
    }

    [Fact]
    public async Task UpsertingACollectionTwiceReturns200TheSecondTime()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        HttpResponseMessage second = await client.PutAsJsonAsync(
            $"/api/v1/collections/{key}", new { name = "Renamed" }, Json);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpsertsADocumentAndChunksIt()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/v1/collections/{key}/documents/publishing",
            Doc("tr", "Menüyü yayınlama", "# Menüyü yayınlama\n\n## Adımlar\n\nMenüyü yayınlamak için."),
            Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("changed").GetBoolean().Should().BeTrue();
        body.GetProperty("revision").GetInt32().Should().Be(1);
        body.GetProperty("chunks").GetProperty("created").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ReUpsertingUnchangedContentIsANoOp()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);
        object doc = Doc("tr", "Başlık", "# Başlık\n\nGövde metni.");

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/a", doc, Json);
        HttpResponseMessage again = await client.PutAsJsonAsync(
            $"/api/v1/collections/{key}/documents/a", doc, Json);

        JsonElement body = await again.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("changed").GetBoolean().Should().BeFalse();
        body.GetProperty("revision").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ForceReIngestsUnchangedContent()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);
        object doc = Doc("tr", "Başlık", "# Başlık\n\nGövde.");

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/a", doc, Json);
        HttpResponseMessage forced = await client.PutAsJsonAsync(
            $"/api/v1/collections/{key}/documents/a?force=true", doc, Json);

        JsonElement body = await forced.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("changed").GetBoolean().Should().BeTrue();
        body.GetProperty("revision").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ChangingOneSectionReChunksOnlyThatSection()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        const string original = "# T\n\n## A\n\nFirst section.\n\n## B\n\nSecond section.";
        const string edited = "# T\n\n## A\n\nFirst section.\n\n## B\n\nSecond section rewritten.";

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/a", Doc("en", "T", original), Json);
        HttpResponseMessage second = await client.PutAsJsonAsync(
            $"/api/v1/collections/{key}/documents/a", Doc("en", "T", edited), Json);

        JsonElement chunks = (await second.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("chunks");

        chunks.GetProperty("reused").GetInt32().Should().BeGreaterThan(0,
            "re-uploading a document where one paragraph changed must not re-create every chunk");
        chunks.GetProperty("created").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RevisionsAccumulate()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/a", Doc("en", "T", "one"), Json);
        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/a", Doc("en", "T", "two"), Json);

        JsonElement revisions = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/collections/{key}/documents/a/revisions", Json);

        revisions.GetProperty("items").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task GetsADocumentWithItsCurrentContent()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/a",
            Doc("en", "Title", "# Title\n\nBody here."), Json);

        JsonElement document = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/collections/{key}/documents/a", Json);

        document.GetProperty("title").GetString().Should().Be("Title");
        document.GetProperty("content").GetString().Should().Contain("Body here.");
        document.GetProperty("chunkCount").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TheSameKeyCoexistsInSeveralLocales()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/shared",
            Doc("tr", "Türkçe", "# Türkçe\n\nİçerik."), Json);
        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/shared",
            Doc("en", "English", "# English\n\nContent."), Json);

        HttpResponseMessage ambiguous = await client.GetAsync($"/api/v1/collections/{key}/documents/shared");
        ambiguous.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        JsonElement turkish = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/collections/{key}/documents/shared?locale=tr", Json);
        turkish.GetProperty("title").GetString().Should().Be("Türkçe");
    }

    [Fact]
    public async Task SoftDeleteHidesTheDocumentButKeepsItsRows()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/gone",
            Doc("en", "T", "# T\n\nBody."), Json);

        HttpResponseMessage deleted = await client.DeleteAsync($"/api/v1/collections/{key}/documents/gone");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        HttpResponseMessage after = await client.GetAsync($"/api/v1/collections/{key}/documents/gone");
        after.StatusCode.Should().Be(HttpStatusCode.NotFound);

        long rows = await _host.ScalarAsync<long>(
            $"SELECT count(*) FROM {_host.Schema}.documents WHERE external_key = 'gone' AND deleted_at IS NOT NULL");
        rows.Should().Be(1, "soft delete keeps the row");
    }

    [Fact]
    public async Task ReUpsertingAfterDeleteRevivesTheDocument()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/a", Doc("en", "T", "body"), Json);
        await client.DeleteAsync($"/api/v1/collections/{key}/documents/a");
        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/a", Doc("en", "T", "body"), Json);

        HttpResponseMessage after = await client.GetAsync($"/api/v1/collections/{key}/documents/a");
        after.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BulkUpsertReportsPerItemResults()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/collections/{key}/documents:bulk",
            new
            {
                documents = new object[]
                {
                    new { externalKey = "a", locale = "tr", title = "A", content = "# A\n\nBir." },
                    new { externalKey = "b", locale = "en", title = "B", content = "# B\n\nTwo." },
                    new { externalKey = "c", locale = "!!bad!!", title = "C", content = "x" },
                },
            },
            Json);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        body.GetProperty("total").GetInt32().Should().Be(3);
        body.GetProperty("changed").GetInt32().Should().Be(2);

        JsonElement results = body.GetProperty("results");
        results[2].GetProperty("status").GetInt32().Should().Be(400);
        results[2].GetProperty("error").GetString().Should().Contain("BCP-47");
    }

    [Fact]
    public async Task RejectsAMalformedLocale()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/v1/collections/{key}/documents/a", Doc("türkçe", "T", "body"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RejectsEmptyContent()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/v1/collections/{key}/documents/a", Doc("en", "T", "   "), Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpsertingIntoAnUnknownCollectionIs404()
    {
        using HttpClient client = Admin();

        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v1/collections/does-not-exist/documents/a", Doc("en", "T", "body"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeletingACollectionCascadesToItsDocuments()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/a", Doc("en", "T", "body"), Json);
        await client.DeleteAsync($"/api/v1/collections/{key}");

        long remaining = await _host.ScalarAsync<long>(
            $"SELECT count(*) FROM {_host.Schema}.documents d "
            + $"JOIN {_host.Schema}.collections c ON c.id = d.collection_id WHERE c.key = '{key}'");

        remaining.Should().Be(0);
    }

    [Fact]
    public async Task ListsDocumentsWithAStableCursor()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        for (int i = 0; i < 5; i++)
        {
            await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/doc-{i}",
                Doc("en", $"T{i}", $"# T{i}\n\nBody {i}."), Json);
        }

        JsonElement first = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/collections/{key}/documents?limit=2", Json);

        first.GetProperty("items").GetArrayLength().Should().Be(2);
        first.TryGetProperty("nextCursor", out JsonElement cursor).Should().BeTrue();

        JsonElement second = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/collections/{key}/documents?limit=2&cursor={Uri.EscapeDataString(cursor.GetString()!)}", Json);

        second.GetProperty("items")[0].GetProperty("externalKey").GetString()
            .Should().Be("doc-2");
    }

    [Fact]
    public async Task ReportsATranslationThatHasFallenBehindItsSource()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/article",
            Doc("tr", "Türkçe", "# Türkçe\n\nİlk sürüm."), Json);

        JsonElement source = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/collections/{key}/documents/article?locale=tr", Json);
        string sourceHash = source.GetProperty("contentHash").GetString()!;

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/article",
            new
            {
                locale = "en",
                title = "English",
                content = "# English\n\nFirst version.",
                sourceLocale = "tr",
                sourceContentHash = sourceHash,
            },
            Json);

        JsonElement fresh = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/collections/{key}/documents?staleAgainst=tr", Json);
        fresh.GetProperty("items").GetArrayLength().Should().Be(0,
            "the translation matches the source hash it recorded");

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/article",
            Doc("tr", "Türkçe", "# Türkçe\n\nİkinci sürüm."), Json);

        JsonElement stale = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/collections/{key}/documents?staleAgainst=tr", Json);

        stale.GetProperty("items").GetArrayLength().Should().Be(1);
        stale.GetProperty("items")[0].GetProperty("locale").GetString().Should().Be("en");
    }

    [Fact]
    public async Task StatsReportTheTenantsOwnCountsOnly()
    {
        using HttpClient client = Admin();
        string key = await NewCollectionAsync(client);

        await client.PutAsJsonAsync($"/api/v1/collections/{key}/documents/a",
            Doc("en", "T", "# T\n\nBody."), Json);

        JsonElement stats = await client.GetFromJsonAsync<JsonElement>("/api/v1/stats", Json);

        stats.GetProperty("schema").GetString().Should().Be(_host.Schema);
        stats.GetProperty("documentCount").GetInt32().Should().BeGreaterThan(0);
    }
}
