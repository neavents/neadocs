namespace Neadocs.Engine.Tests.Unit.Text;

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Text;

public sealed class ShippedRuleSetTests
{
    private static readonly IReadOnlyDictionary<string, LoadedRuleSet> Loaded =
        RuleSetLoader.Load(directory: null);

    private static readonly NormalizerRegistry Registry = new(Loaded);

    public static TheoryData<string, string, string> SelfTestCases()
    {
        TheoryData<string, string, string> data = [];

        foreach (LoadedRuleSet loaded in Loaded.Values)
        {
            foreach (SelfTestCase test in loaded.Pipeline.SelfTests)
            {
                data.Add(loaded.Pipeline.Tag, test.Input, test.Expected);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SelfTestCases))]
    public void EveryShippedSelfTestPasses(string tag, string input, string expected) =>
        Registry.Resolve(tag).Normalize(input).Should().Be(expected);

    [Fact]
    public void TheShippedSetCoversTheDeclaredLanguages() =>
        Loaded.Keys.Should().Contain(["*", "tr", "ar", "fa", "he"]);

    [Fact]
    public void EveryShippedRuleSetCarriesAtLeastThreeSelfTests() =>
        Loaded.Values.Should().OnlyContain(
            l => l.Pipeline.SelfTests.Count >= RuleOperations.MinimumSelfTests);

    [Fact]
    public void NoShippedRuleSetUsesAnOperationTheRuntimeCannotPerform() =>
        Loaded.Values.SelectMany(l => l.Pipeline.Operations)
            .Should().NotContain(o => o.Name == RuleOperations.NormalizeForm);

    [Theory]
    [InlineData("IST", "ist")]
    [InlineData("İSTANBUL", "istanbul")]
    [InlineData("ŞİFRE", "sifre")]
    [InlineData("sifre", "sifre")]
    // `nasıl` is gone by design, and this case used to assert that it survived. Postgres' Turkish
    // dictionary treats it as a stopword, but only in its diacritic form — folding to `nasil` put
    // it out of the list's reach, and `websearch_to_tsquery` then made it a term every matching
    // document had to contain. A question asked in ordinary Turkish therefore only found documents
    // that happened to use the same question word.
    [InlineData("Menümü nasıl yayınlarım", "menumu yayinlarim")]
    [InlineData("MENÜ", "menu")]
    public void TurkishFoldsTheWayAnOwnerActuallyTypes(string input, string expected) =>
        Registry.Normalize("tr", input).Should().Be(expected);

    [Theory]
    // The folded forms of Postgres' own turkish.stop. Each is unreachable by the dictionary once
    // the text is folded, which is exactly why the pipeline has to remove them itself.
    [InlineData("nasıl")]
    [InlineData("için")]
    [InlineData("çünkü")]
    [InlineData("ve")]
    [InlineData("hiç")]
    public void TurkishFunctionWordsDoNotSurviveIntoAQuery(string word) =>
        Registry.Normalize("tr", word).Should().BeEmpty();

    [Fact]
    public void DroppingFunctionWordsLeavesTheContentWordsIntact() =>
        Registry.Normalize("tr", "menümü ve şifremi nasıl değiştiririm")
            .Should().Be("menumu sifremi degistiririm");

    [Fact]
    public void AStopwordInsideALongerWordIsNotTouched() =>
        // `ne` is a stopword; `nerede` is not, and a substring rule would leave `rede` behind.
        Registry.Normalize("tr", "menü ne zaman yayınlanır")
            .Should().Be("menu zaman yayinlanir");

    [Fact]
    public void AnUndiacriticisedQueryMatchesTheDiacriticisedDocument() =>
        Registry.Normalize("tr", "sifremi unuttum")
            .Should().Be(Registry.Normalize("tr", "şifremi unuttum"));

    [Fact]
    public void TurkishDottedAndDotlessIStayDistinctThroughFolding()
    {
        Registry.Normalize("tr", "IL").Should().Be("il");
        Registry.Normalize("tr", "İL").Should().Be("il");
        Registry.Normalize("tr", "ıl").Should().Be("il");
    }

    [Theory]
    [InlineData("Café", "cafe")]
    [InlineData("Straße", "strasse")]
    [InlineData("ÉCOLE", "ecole")]
    [InlineData("Ærø", "aero")]
    [InlineData("Łódź", "lodz")]
    [InlineData("Привет", "привет")]
    [InlineData("ΑΘΗΝΑ", "αθηνα")]
    public void TheFallbackHandlesEuropeanScripts(string input, string expected) =>
        Registry.Normalize("de", input).Should().Be(expected);

    [Fact]
    public void ApplyingTurkishRulesGloballyWouldBreakOtherLanguages()
    {
        Registry.Normalize("tr", "Café").Should().NotBe("cafe");

        Registry.Normalize("de", "Café").Should().Be("cafe");
    }

    [Fact]
    public void ArabicUnifiesLetterFormsAndDropsHarakat()
    {
        Registry.Normalize("ar", "أحمد").Should().Be("احمد");
        Registry.Normalize("ar", "مَدْرَسَة").Should().Be("مدرسه");
    }

    [Fact]
    public void ArabicStripsTatweelWhichIsNotACombiningMark() =>
        Registry.Normalize("ar", "الــقـائـمة").Should().Be("القايمه");

    [Fact]
    public void ArabicIndicDigitsBecomeAscii()
    {
        Registry.Normalize("ar", "٢٠٢٦").Should().Be("2026");
        Registry.Normalize("fa", "۱۴۰۵").Should().Be("1405");
    }

    [Fact]
    public void BidiControlsAreStrippedSoTheyCannotHideFromAQuery()
    {
        Registry.Normalize("ar", "‏مرحبا‎").Should().Be("مرحبا");
        Registry.Normalize("ar", "‮مرحبا‬").Should().Be("مرحبا");
    }

    [Fact]
    public void ZeroWidthNonJoinerSurvivesInPersianAndIsRemovedInArabic()
    {
        // The example moved off `می‌رود` deliberately, and then off `کتاب‌ها` too: both halves of
        // this one are content words, so it observes the ZWNJ split and nothing else. The earlier
        // examples each had a half that is now a stopword, which would have made this test pass or
        // fail for reasons that have nothing to do with zero-width joiners.
        Registry.Normalize("fa", "دانش‌آموز").Should().Be("دانش اموز");

        Registry.Normalize("ar", "دانش‌آموز").Should().NotContain(" ");
    }

    [Theory]
    // The aspect marker and the plural suffix. Neither is a separate word in Persian — they attach
    // through a zero-width joiner — so the stopword list can only reach them because the previous
    // rule turned that joiner into a space. The two rules are useless apart and, together, make an
    // inflected form index as its own stem.
    [InlineData("می‌رود", "رود")]
    [InlineData("کتاب‌ها", "کتاب")]
    public void SplittingOnZeroWidthNonJoinerPutsPersianCliticsInReachOfTheStopwordList(
        string input,
        string stem) => Registry.Normalize("fa", input).Should().Be(stem);

    [Fact]
    public void HebrewStripsNiqqudAndNormalisesFinalForms()
    {
        Registry.Normalize("he", "שָׁלוֹם").Should().Be("שלומ");
        Registry.Normalize("he", "מלך").Should().Be("מלכ");
    }

    [Fact]
    public void RtlTextIsStoredAndReturnedInLogicalOrder()
    {
        const string arabic = "القائمة";

        string normalized = Registry.Normalize("ar", arabic);

        normalized[0].Should().Be('ا');
        normalized.Should().NotContain("‏").And.NotContain("‎");
    }

    [Theory]
    [InlineData("tr-TR", "tr")]
    [InlineData("tr", "tr")]
    [InlineData("TR", "tr")]
    [InlineData("ar-EG", "ar")]
    [InlineData("de-DE", "*")]
    [InlineData("de", "*")]
    [InlineData("zz", "*")]
    [InlineData("", "*")]
    [InlineData(null, "*")]
    public void ResolvesByLongestPrefixAndNeverFails(string? locale, string expectedTag) =>
        Registry.Resolve(locale).Tag.Should().Be(expectedTag);

    [Fact]
    public void ResolutionNeverThrowsForAnyInput()
    {
        string[] hostile = ["", " ", "---", "tr-", "-tr", "a-b-c-d-e", "🙂", new string('x', 500)];

        foreach (string locale in hostile)
        {
            Registry.Resolve(locale).Should().NotBeNull();
        }
    }

    [Fact]
    public void EveryRuleSetHasADistinctHash() =>
        Loaded.Values.Select(l => l.Pipeline.Hash).Should().OnlyHaveUniqueItems();

    [Fact]
    public void HashesAreStableAcrossReloads()
    {
        IReadOnlyDictionary<string, LoadedRuleSet> again = RuleSetLoader.Load(directory: null);

        foreach (KeyValuePair<string, LoadedRuleSet> entry in Loaded)
        {
            again[entry.Key].Pipeline.Hash.Should().Be(entry.Value.Pipeline.Hash);
        }
    }

    [Fact]
    public void NormalisingIsIdempotentForEveryShippedRuleSet()
    {
        foreach (LoadedRuleSet loaded in Loaded.Values)
        {
            foreach (SelfTestCase test in loaded.Pipeline.SelfTests)
            {
                string once = loaded.Pipeline.Normalize(test.Input);

                loaded.Pipeline.Normalize(once).Should().Be(once,
                    $"[{loaded.Pipeline.Tag}] '{test.Input}'");
            }
        }
    }
}
