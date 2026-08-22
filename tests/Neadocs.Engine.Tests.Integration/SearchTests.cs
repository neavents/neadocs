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

[Collection(NeadocsCollection.Name)]
public sealed class SearchTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NeadocsTestHost _host;
    private string _collection = string.Empty;

    public SearchTests(NeadocsTestHost host) => _host = host;

    private HttpClient Admin() => _host.ClientWithKey(NeadocsTestHost.AdminKey);

    public async Task InitializeAsync()
    {
        _collection = "search-" + Guid.NewGuid().ToString("N")[..8];

        using HttpClient client = Admin();
        await client.PutAsJsonAsync($"/api/v1/collections/{_collection}", new { name = "Search" }, Json);

        await SeedAsync(client, "publishing-a-menu", "tr", "Menüyü yayınlama",
            "# Menüyü yayınlama\n\n## Adımlar\n\nMenüyü yayınlamak için düzenle ekranına gidin ve yayınla düğmesine basın.",
            new { section = "menus", audience = "owner" });

        await SeedAsync(client, "password-reset", "tr", "Şifremi unuttum",
            "# Şifremi unuttum\n\n## Sıfırlama\n\nŞifrenizi sıfırlamak için giriş ekranındaki bağlantıyı kullanın.",
            new { section = "account", audience = "owner" });

        await SeedAsync(client, "qr-troubleshooting", "tr", "Karekod çalışmıyor",
            "# Karekod çalışmıyor\n\n## Kontrol\n\nKarekod okunmuyorsa masadaki etiketi değiştirin.",
            new { section = "qr", audience = "owner" });

        await SeedAsync(client, "publishing-a-menu", "en", "Publishing a menu",
            "# Publishing a menu\n\n## Steps\n\nOpen the editor and press publish to make the menu live.",
            new { section = "menus", audience = "owner" });

        await SeedAsync(client, "cafe-guide", "fr", "Le café",
            "# Le café\n\n## Détails\n\nUn guide très détaillé pour le café et la crème brûlée.",
            new { section = "misc" });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedAsync(
        HttpClient client, string key, string locale, string title, string content, object metadata)
    {
        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/v1/collections/{_collection}/documents/{key}",
            new { locale, title, content, metadata },
            Json);

        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> SearchAsync(object request)
    {
        using HttpClient client = Admin();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/collections/{_collection}/search", request, Json);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private static List<string> KeysOf(JsonElement result) =>
        [.. result.GetProperty("hits").EnumerateArray()
            .Select(h => h.GetProperty("externalKey").GetString()!)];

    [Fact]
    public async Task FindsATurkishPhraseTypedWithItsDiacritics()
    {
        JsonElement result = await SearchAsync(new { query = "menüyü yayınlama", locale = "tr" });

        KeysOf(result).Should().Contain("publishing-a-menu");
    }

    [Fact]
    public async Task FindsATurkishPhraseTypedWithoutDiacritics()
    {
        JsonElement result = await SearchAsync(new { query = "menuyu yayinlama", locale = "tr" });

        KeysOf(result).Should().Contain("publishing-a-menu",
            "an owner on a phone types without diacritics, and this is the case the whole folding "
            + "layer exists for");
    }

    [Fact]
    public async Task FindsThePasswordArticleFromAnUndiacriticisedQuery()
    {
        JsonElement result = await SearchAsync(new { query = "sifremi unuttum", locale = "tr" });

        KeysOf(result).Should().Contain("password-reset");
        KeysOf(result)[0].Should().Be("password-reset");
    }

    [Fact]
    public async Task IsCaseInsensitiveAcrossTurkishDottedI()
    {
        JsonElement upper = await SearchAsync(new { query = "ŞİFREMİ", locale = "tr" });
        JsonElement lower = await SearchAsync(new { query = "sifremi", locale = "tr" });

        KeysOf(upper).Should().Contain("password-reset");
        KeysOf(lower).Should().Contain("password-reset");
    }

    [Fact]
    public async Task FindsADocumentDespiteTurkishAgglutination()
    {
        JsonElement result = await SearchAsync(new { query = "menümü nasıl yayınlarım", locale = "tr" });

        KeysOf(result).Should().Contain("publishing-a-menu",
            "the query says menumu/yayinlarim and the document says menuyu/yayinlama; those share "
            + "no token, so only the trigram fallback can bridge them. Turkish is agglutinative "
            + "and this is the single most likely way a real question is phrased.");
    }

    [Fact]
    public async Task AgglutinatedFormsOfTheSameStemAllFindTheDocument()
    {
        foreach (string query in new[] { "menüyü", "menümü", "menülerimiz", "menüsünü" })
        {
            JsonElement result = await SearchAsync(new { query, locale = "tr" });

            KeysOf(result).Should().Contain("publishing-a-menu", $"query '{query}'");
        }
    }

    [Fact]
    public async Task FindsAFrenchDocumentFromAnUnaccentedQuery()
    {
        JsonElement result = await SearchAsync(new { query = "cafe creme brulee", locale = "fr" });

        KeysOf(result).Should().Contain("cafe-guide",
            "the fallback rule set folds French diacritics with no rule file of its own");
    }

    [Fact]
    public async Task ReturnsNothingForAQueryThatMatchesNoDocument()
    {
        JsonElement result = await SearchAsync(new { query = "kesinlikle alakasiz bir sorgu", locale = "tr" });

        result.GetProperty("hits").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AppliesAMetadataFilter()
    {
        JsonElement all = await SearchAsync(new { query = "menü", locale = "tr" });
        JsonElement filtered = await SearchAsync(new
        {
            query = "menü",
            locale = "tr",
            filter = new { section = "account" },
        });

        KeysOf(all).Should().Contain("publishing-a-menu");
        KeysOf(filtered).Should().NotContain("publishing-a-menu");
    }

    [Fact]
    public async Task RestrictsResultsToTheRequestedLocaleChain()
    {
        JsonElement turkish = await SearchAsync(new { query = "yayinlama", locale = "tr" });

        turkish.GetProperty("hits").EnumerateArray()
            .Should().OnlyContain(h => h.GetProperty("locale").GetString() == "tr"
                                       || h.GetProperty("locale").GetString() == "en");
    }

    [Fact]
    public async Task ExactLocaleHitsOutrankFallbackHits()
    {
        JsonElement result = await SearchAsync(new { query = "publishing a menu", locale = "tr" });
        List<JsonElement> hits = [.. result.GetProperty("hits").EnumerateArray()];

        for (int i = 1; i < hits.Count; i++)
        {
            double previous = hits[i - 1].GetProperty("score").GetDouble();
            double current = hits[i].GetProperty("score").GetDouble();

            if (Math.Abs(previous - current) > 1e-9)
            {
                continue;
            }

            bool previousIsFallback = hits[i - 1].GetProperty("locale").GetString() != "tr";
            bool currentIsExact = hits[i].GetProperty("locale").GetString() == "tr";

            (previousIsFallback && currentIsExact).Should().BeFalse(
                "at equal fused score the exact-locale hit ranks first; a fallback hit with a "
                + "genuinely higher score is allowed to win on merit");
        }
    }

    [Fact]
    public async Task ReturnsASnippetAndLogicalHighlightOffsets()
    {
        JsonElement result = await SearchAsync(new { query = "yayinlama", locale = "tr" });
        JsonElement hit = result.GetProperty("hits")[0];

        hit.GetProperty("snippet").GetString().Should().NotBeNullOrEmpty();

        foreach (JsonElement highlight in hit.GetProperty("highlights").EnumerateArray())
        {
            highlight.GetProperty("start").GetInt32().Should().BeGreaterThanOrEqualTo(0);
            highlight.GetProperty("length").GetInt32().Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task ReturnsTheHeadingPathAsAnArray()
    {
        JsonElement result = await SearchAsync(new { query = "yayinlama", locale = "tr" });
        JsonElement hit = result.GetProperty("hits")[0];

        hit.GetProperty("headingPath").ValueKind.Should().Be(JsonValueKind.Array,
            "a pre-joined string would bake a reading direction into stored data");
    }

    [Fact]
    public async Task ExplainReportsTheLexicalRankWhenAsked()
    {
        JsonElement result = await SearchAsync(new { query = "yayinlama", locale = "tr", explain = true });

        result.GetProperty("hits")[0].GetProperty("explain")
            .GetProperty("lexicalRank").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ExplainIsAbsentUnlessRequested()
    {
        JsonElement result = await SearchAsync(new { query = "yayinlama", locale = "tr" });

        result.GetProperty("hits")[0].TryGetProperty("explain", out _).Should().BeFalse();
    }

    [Fact]
    public async Task HybridSilentlyRunsLexicalAndSaysSoWhenNoModelIsConfigured()
    {
        JsonElement result = await SearchAsync(new { query = "yayinlama", locale = "tr", mode = "hybrid" });

        result.GetProperty("degraded").GetBoolean().Should().BeTrue(
            "the client must be able to tell that hybrid fell back to lexical");
        result.GetProperty("mode").GetString().Should().Be("lexical");
    }

    [Fact]
    public async Task VectorModeIsRejectedWhenNoModelIsConfigured()
    {
        using HttpClient client = Admin();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/collections/{_collection}/search",
            new { query = "yayinlama", locale = "tr", mode = "vector" },
            Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("no embedding model is configured");
    }

    [Fact]
    public async Task PaginationIsStableAcrossIdenticalCalls()
    {
        JsonElement first = await SearchAsync(new { query = "menü", locale = "tr", limit = 3 });
        JsonElement second = await SearchAsync(new { query = "menü", locale = "tr", limit = 3 });

        KeysOf(first).Should().Equal(KeysOf(second));
    }

    [Fact]
    public async Task RespectsTheRequestedLimit()
    {
        JsonElement result = await SearchAsync(new { query = "menü", locale = "tr", limit = 1 });

        result.GetProperty("hits").GetArrayLength().Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task ASoftDeletedDocumentDisappearsFromSearch()
    {
        using HttpClient client = Admin();

        await SeedAsync(client, "temporary", "tr", "Geçici", "# Geçici\n\nBenzersizkelimeburada.", new { });

        KeysOf(await SearchAsync(new { query = "benzersizkelimeburada", locale = "tr" }))
            .Should().Contain("temporary");

        await client.DeleteAsync($"/api/v1/collections/{_collection}/documents/temporary?locale=tr");

        KeysOf(await SearchAsync(new { query = "benzersizkelimeburada", locale = "tr" }))
            .Should().NotContain("temporary");
    }

    [Fact]
    public async Task RejectsAnEmptyQuery()
    {
        using HttpClient client = Admin();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/collections/{_collection}/search", new { query = "   " }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RejectsAnOverlongQuery()
    {
        using HttpClient client = Admin();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/collections/{_collection}/search",
            new { query = new string('a', 1000) },
            Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SearchingAnUnknownCollectionIs404()
    {
        using HttpClient client = Admin();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/collections/no-such-collection/search", new { query = "x" }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReportsHowLongItTook()
    {
        JsonElement result = await SearchAsync(new { query = "menü", locale = "tr" });

        result.GetProperty("tookMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task FindsAnArabicDocumentWithItsHarakatStripped()
    {
        using HttpClient client = Admin();

        await SeedAsync(client, "arabic-guide", "ar", "القائمة",
            "# القائمة\n\n## النشر\n\nلِنَشْر القائمة اتبع الخطوات المذكورة.", new { });

        JsonElement result = await SearchAsync(new { query = "لنشر القائمة", locale = "ar" });

        KeysOf(result).Should().Contain("arabic-guide",
            "the query carries no harakat and the document does, so only folding makes them meet");
    }
}
