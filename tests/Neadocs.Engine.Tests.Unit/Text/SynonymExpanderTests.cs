namespace Neadocs.Engine.Tests.Unit.Text;

using System.Collections.Generic;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Text;

public sealed class SynonymExpanderTests
{
    private static readonly NormalizerRegistry Normalizers =
        new(RuleSetLoader.Load(directory: null));

    private static SynonymExpander Build(
        Dictionary<string, List<SynonymGroupOptions>> synonyms,
        Dictionary<string, List<string>>? fallback = null) =>
        new(
            new TextOptions
            {
                Locales = ["tr", "en", "de"],
                DefaultLocale = "tr",
                Synonyms = synonyms,
                LocaleFallback = fallback ?? [],
            },
            Normalizers);

    private static SynonymExpander Turkish() => Build(new()
    {
        ["tr"] =
        [
            new() { Terms = ["karekod", "qr kod", "qr"] },
            new() { Terms = ["yayınla", "yayınlama"] },
        ],
    });

    [Fact]
    public void ExpandsAMatchedTermToItsSiblings() =>
        Turkish().Expand("tr", "karekod").Should().BeEquivalentTo(["qr kod", "qr"]);

    [Fact]
    public void ExpandsFromAnyMemberOfTheGroup() =>
        Turkish().Expand("tr", "qr").Should().BeEquivalentTo(["karekod", "qr kod"]);

    [Fact]
    public void MatchesAMultiWordTermAsAPhrase() =>
        Turkish().Expand("tr", "qr kod").Should().Contain("karekod");

    [Fact]
    public void ReturnsNothingWhenNoGroupMatches() =>
        Turkish().Expand("tr", "tamamen alakasiz").Should().BeEmpty();

    [Fact]
    public void ReturnsNothingForAnEmptyQuery()
    {
        Turkish().Expand("tr", "").Should().BeEmpty();
        Turkish().Expand("tr", "   ").Should().BeEmpty();
    }

    [Fact]
    public void TermsAreNormalisedOnLoadSoFilesCanBeWrittenNaturally()
    {
        SynonymExpander expander = Turkish();

        expander.Expand("tr", Normalizers.Normalize("tr", "yayınla"))
            .Should().Contain(Normalizers.Normalize("tr", "yayınlama"));
    }

    [Fact]
    public void AQueryTypedWithoutDiacriticsStillMatchesItsGroup()
    {
        SynonymExpander expander = Turkish();

        expander.Expand("tr", Normalizers.Normalize("tr", "yayinla"))
            .Should().NotBeEmpty();
    }

    [Fact]
    public void AGermanGroupNeverFiresOnTurkishText()
    {
        SynonymExpander expander = Build(new()
        {
            ["de"] = [new() { Terms = ["karekod", "etwas"] }],
        });

        expander.Expand("tr", "karekod").Should().BeEmpty();
        expander.Expand("de", "karekod").Should().Contain("etwas");
    }

    [Fact]
    public void FollowsTheConfiguredFallbackChain()
    {
        SynonymExpander expander = Build(
            new() { ["en"] = [new() { Terms = ["publish", "go live"] }] },
            new() { ["tr"] = ["en"] });

        expander.Expand("tr", "publish").Should().Contain("go live");
    }

    [Fact]
    public void TheLocaleChainStartsWithTheRequestedLocale()
    {
        SynonymExpander expander = Build([], new() { ["tr"] = ["en"] });

        expander.LocaleChain("tr").Should().Equal(["tr", "en"]);
        expander.LocaleChain("en").Should().Equal(["en"]);
    }

    [Fact]
    public void TheLocaleChainNormalisesItsInput()
    {
        SynonymExpander expander = Build([], new() { ["tr"] = ["en"] });

        expander.LocaleChain("TR_tr").Should().StartWith("tr-tr");
    }

    [Fact]
    public void TheLocaleChainIsEmptyForNoLocale() =>
        Build([]).LocaleChain(null).Should().BeEmpty();

    [Fact]
    public void NeverReturnsATermAlreadyPresentInTheQuery() =>
        Turkish().Expand("tr", "karekod qr").Should().BeEquivalentTo(["qr kod"]);

    [Fact]
    public void NeverReturnsDuplicates()
    {
        SynonymExpander expander = Build(new()
        {
            ["tr"] =
            [
                new() { Terms = ["a", "shared"] },
                new() { Terms = ["b", "shared"] },
            ],
        });

        expander.Expand("tr", "a b").Should().BeEquivalentTo(["shared"]);
    }

    [Fact]
    public void DropsAGroupThatCollapsesToFewerThanTwoDistinctTerms()
    {
        SynonymExpander expander = Build(new()
        {
            ["tr"] = [new() { Terms = ["sifre", "şifre"] }],
        });

        expander.GroupCount.Should().Be(0,
            "both terms normalise to the same string, so the group would expand to nothing");
    }

    [Fact]
    public void MatchesWholeTokensRatherThanSubstrings()
    {
        SynonymExpander expander = Build(new()
        {
            ["tr"] = [new() { Terms = ["qr", "karekod"] }],
        });

        expander.Expand("tr", "qrcode").Should().BeEmpty();
        expander.Expand("tr", "qr").Should().Contain("karekod");
    }

    [Fact]
    public void CountsTheLoadedGroups() =>
        Turkish().GroupCount.Should().Be(2);

    [Fact]
    public void HandlesNoSynonymsAtAll()
    {
        SynonymExpander expander = Build([]);

        expander.GroupCount.Should().Be(0);
        expander.Expand("tr", "anything").Should().BeEmpty();
    }
}
