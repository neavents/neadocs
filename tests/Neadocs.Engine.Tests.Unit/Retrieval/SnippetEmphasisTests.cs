namespace Neadocs.Engine.Tests.Unit.Retrieval;

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Neadocs.Engine.Features;
using Neadocs.Engine.Infrastructure.Retrieval;
using Neadocs.Engine.Infrastructure.Text;

/// <summary>
/// Emphasis for the languages the database cannot emphasise.
/// </summary>
/// <remarks>
/// <c>ts_headline</c> is handed the raw content and a query built from folded, stemmed text, so it
/// only ever agrees with itself for a language whose folded form equals its raw form. English is
/// such a language; Turkish is not, and every Turkish result arrived with no markers and no
/// highlights — which renders as a plain grey paragraph, indistinguishable from a working one.
/// </remarks>
public sealed class SnippetEmphasisTests
{
    private static readonly NormalizerRegistry Registry = new(RuleSetLoader.Load(directory: null));

    private static string Emphasise(string snippet, string locale, params string[] terms) =>
        LexicalSearch.EmphasiseFolded(snippet, terms, Registry.Resolve(locale));

    [Fact]
    public void ItMarksATurkishWordThatOnlyMatchesOnceFolded()
    {
        // `şifremi` folds to `sifremi`, which is what the query became. The two are only comparable
        // on the folded axis, which is the axis ts_headline never sees.
        Emphasise("Giriş ekranında Şifremi unuttum bağlantısı", "tr", "sifremi")
            .Should().Contain("<em>Şifremi</em>");
    }

    [Fact]
    public void ItKeepsTheReadersOwnCharacters()
    {
        // Only markers are inserted. Rewriting the text to its folded form would hand a Turkish
        // reader de-diacriticised Turkish, which reads as a rendering fault.
        string result = Emphasise("Şifremi unuttum", "tr", "sifremi");

        result.Should().Contain("Şifremi").And.NotContain("Sifremi");
    }

    [Fact]
    public void ItMarksAnInflectedFormAgainstAnInflectedQuery()
    {
        // Neither exact match nor snowball connects these; the shared truncation does.
        Emphasise("Masalarımı düzenle", "tr", "masa")
            .Should().Contain("<em>Masalarımı</em>");
    }

    [Fact]
    public void ItLeavesFunctionWordsAlone()
    {
        // `nasıl` is dropped by the pipeline, so it carries no information about why this result
        // matched. Lighting it up would be worse than lighting up nothing.
        Emphasise("Menü nasıl yayınlanır", "tr", "nasil")
            .Should().NotContain("<em>");
    }

    [Fact]
    public void ItDefersEntirelyWhenTheDatabaseAlreadyEmphasised()
    {
        // English was working. A second pass would nest markers inside markers.
        const string already = "Press <em>Forgot</em> password";

        Emphasise(already, "en", "forgot", "password").Should().Be(already);
    }

    [Fact]
    public void ItMarksEveryOccurrence()
    {
        Emphasise("Şifremi ve yine şifremi", "tr", "sifremi")
            .Should().Contain("<em>Şifremi</em>").And.Contain("<em>şifremi</em>");
    }

    [Fact]
    public void ItPreservesPunctuationAndMarkupAroundAWord()
    {
        // Snippets carry the source's markdown. Splitting on letters and digits means the
        // surrounding characters come back byte for byte.
        Emphasise("**Şifremi unuttum** bağlantısı.", "tr", "sifremi")
            .Should().Be("**<em>Şifremi</em> unuttum** bağlantısı.");
    }

    [Fact]
    public void ItDoesNothingWithoutTerms() =>
        Emphasise("Şifremi unuttum", "tr").Should().Be("Şifremi unuttum");

    [Fact]
    public void ItDoesNotMarkAnUnrelatedWord() =>
        Emphasise("Masa ekleme adımları", "tr", "sifremi").Should().NotContain("<em>");

    [Fact]
    public void TheMarkersItAddsAreReadBackAsHighlights()
    {
        // The two have to agree: `HighlightsFrom` derives offsets by finding the emphasised term in
        // the raw content, so emphasis that the offsets do not follow would highlight one thing and
        // point at another.
        const string content = "Giriş ekranında Şifremi unuttum bağlantısı";

        string snippet = Emphasise(content, "tr", "sifremi");
        List<Highlight> highlights = LexicalSearch.HighlightsFrom(snippet, content);

        highlights.Should().ContainSingle();
        content.Substring(highlights[0].Start, highlights[0].Length).Should().Be("Şifremi");
    }

    [Fact]
    public void AnExcerptIsCentredOnTheMatch()
    {
        // A chunk found only by the fuzzy pass has no headline, and used to be previewed with its
        // first 240 characters — an opening that need not mention the query anywhere.
        string content =
            string.Join(' ', Enumerable.Repeat("dolgu", 80))
            + " Şifremi unuttum "
            + string.Join(' ', Enumerable.Repeat("kuyruk", 80));

        string excerpt = LexicalSearch.ExcerptAround(
            content, ["sifremi"], Registry.Resolve("tr"), 240);

        excerpt.Should().Contain("<em>Şifremi</em>");
        excerpt.Should().StartWith("…").And.EndWith("…");
        excerpt.Length.Should().BeLessThan(300);
    }

    [Fact]
    public void AnExcerptFallsBackToTheOpeningWhenNothingMatches()
    {
        // The fuzzy pass matches on character similarity, so there may be no whole word to point
        // at. Showing the opening is the honest answer; inventing a match is not.
        string excerpt = LexicalSearch.ExcerptAround(
            "Masa ekleme adımları burada anlatılır.", ["sifremi"], Registry.Resolve("tr"), 240);

        excerpt.Should().StartWith("Masa").And.NotContain("<em>");
    }

    [Fact]
    public void AnExcerptNeverOpensOrClosesMidWord()
    {
        string content = "birinci ikinci ucuncu dorduncu Şifremi besinci altinci yedinci sekizinci";

        string excerpt = LexicalSearch.ExcerptAround(
            content, ["sifremi"], Registry.Resolve("tr"), 24).Trim('…');

        // Whatever window was chosen, its edges land on word boundaries.
        content.Should().Contain(excerpt.Replace("<em>", "").Replace("</em>", ""));
    }

    [Fact]
    public void AShortChunkIsReturnedWholeWithNoEllipsis()
    {
        LexicalSearch.ExcerptAround("Şifremi unuttum", ["sifremi"], Registry.Resolve("tr"), 240)
            .Should().Be("<em>Şifremi</em> unuttum");
    }

    [Fact]
    public void OneEnormousTokenCannotSwallowTheWindow()
    {
        // A URL, a base64 blob, minified output pasted into a document. Snapping to a word boundary
        // has to be bounded, or the edge walks the whole token and the window lands somewhere the
        // match is not — the exact outcome centring exists to prevent.
        string content = new string('x', 400) + " Şifremi unuttum " + new string('y', 400);

        LexicalSearch.ExcerptAround(content, ["sifremi"], Registry.Resolve("tr"), 240)
            .Should().Contain("<em>Şifremi</em>");
    }
}
