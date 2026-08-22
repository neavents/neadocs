namespace Neadocs.Engine.Tests.Unit.Text;

using System;
using System.Globalization;
using System.Text;
using FluentAssertions;

public sealed class RuntimeCasingFactsTests
{
    [Fact]
    public void TheSuiteRunsUnderTheSameGlobalizationModeAsTheEngine()
    {
        bool invariant = AppContext.TryGetSwitch(
            "System.Globalization.Invariant", out bool enabled) && enabled;

        invariant.Should().BeTrue(
            "the text layer's behaviour depends on ICU being absent, so a suite that runs with "
            + "ICU present would verify rules the engine never executes");
    }

    [Fact]
    public void AskingForANamedCultureThrowsRatherThanSilentlyMisbehaving()
    {
        Action act = () => _ = new CultureInfo("tr-TR");

        act.Should().Throw<CultureNotFoundException>(
            "with ICU removed there is no tr-TR at all; the failure is loud rather than a culture "
            + "that quietly cases text the wrong way");
    }

    [Theory]
    [InlineData("ÉCOLE", "école")]
    [InlineData("ПРИВЕТ", "привет")]
    [InlineData("ΑΘΗΝΑ", "αθηνα")]
    [InlineData("ÄÖÜ", "äöü")]
    [InlineData("ÇĞŞ", "çğş")]
    public void InvariantLowercasingStillHandlesNonAscii(string input, string expected) =>
        input.ToLowerInvariant().Should().Be(expected,
            "the '*' rule set lowercases with mode 'invariant' and would be broken for every "
            + "non-Latin script if this were ASCII-only");

    [Fact]
    public void InvariantLowercasingIsWrongForTurkishDottedCapitalI() =>
        "İ".ToLowerInvariant().Should().NotBe("i",
            "this is precisely why the tr rule set maps İ to i before lowercasing");

    [Fact]
    public void UnicodeNormalizationIsANoOpAndCannotBeUsedToFoldDiacritics()
    {
        const string precomposed = "Café";

        string decomposed = precomposed.Normalize(NormalizationForm.FormD);

        decomposed.Should().Be(precomposed,
            "globalization-invariant mode has no normalization tables, so Normalize returns the "
            + "input unchanged. Any rule set folding diacritics via FormD plus a NonSpacingMark "
            + "strip would silently do nothing, and 'Café' would never match 'cafe'.");

        precomposed.IsNormalized(NormalizationForm.FormD).Should().BeTrue(
            "IsNormalized agrees with Normalize, so the no-op is not even detectable by asking");
    }

    [Fact]
    public void AlreadyDecomposedTextStillLosesItsMarksToTheStripOperation()
    {
        const string decomposed = "Café";

        CharUnicodeInfo.GetUnicodeCategory(decomposed[^1])
            .Should().Be(UnicodeCategory.NonSpacingMark,
                "stripUnicodeCategory still works; it is only the decomposition step that does not");
    }

    [Fact]
    public void UnicodeCategoryDataIsAvailableWithoutIcu()
    {
        CharUnicodeInfo.GetUnicodeCategory('́')
            .Should().Be(UnicodeCategory.NonSpacingMark);

        CharUnicodeInfo.GetUnicodeCategory('‌')
            .Should().Be(UnicodeCategory.Format);

        CharUnicodeInfo.GetUnicodeCategory('ـ')
            .Should().Be(UnicodeCategory.ModifierLetter);
    }

    [Fact]
    public void OrdinalComparisonIsUnaffected()
    {
        string.Equals("abc", "abc", StringComparison.Ordinal).Should().BeTrue();
        string.Equals("ABC", "abc", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        string.Equals("ı", "I", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }
}
