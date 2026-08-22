namespace Neadocs.Engine.Tests.Unit.Text;

using FluentAssertions;
using Neadocs.Engine.Infrastructure.Text;

public sealed class LocaleTagTests
{
    [Theory]
    [InlineData("tr", "tr")]
    [InlineData("TR", "tr")]
    [InlineData("tr-TR", "tr-tr")]
    [InlineData("tr_TR", "tr-tr")]
    [InlineData("TR_tr", "tr-tr")]
    [InlineData("  en-GB  ", "en-gb")]
    [InlineData("zh-Hant-TW", "zh-hant-tw")]
    public void NormalizesCaseUnderscoresAndWhitespace(string input, string expected) =>
        LocaleTag.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizesNothingToEmpty(string? input) =>
        LocaleTag.Normalize(input).Should().BeEmpty();

    [Fact]
    public void NormalizationIsIdempotent()
    {
        string once = LocaleTag.Normalize("tr_TR");

        LocaleTag.Normalize(once).Should().Be(once);
    }

    [Fact]
    public void LowercasingIsAsciiOnlyAndNeverCultureSensitive()
    {
        LocaleTag.Normalize("I").Should().Be("i");
        LocaleTag.Normalize("İ").Should().Be("İ");
    }

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("ara")]
    [InlineData("tr-tr")]
    [InlineData("en-gb")]
    [InlineData("zh-hant-tw")]
    [InlineData("de-1901")]
    [InlineData("sr-latn-rs")]
    public void AcceptsWellFormedTags(string tag) =>
        LocaleTag.IsWellFormed(tag).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("t")]
    [InlineData("türk")]
    [InlineData("TR")]
    [InlineData("tr_tr")]
    [InlineData("tr-")]
    [InlineData("-tr")]
    [InlineData("tr--tr")]
    [InlineData("toolong")]
    [InlineData("tr-verylongsubtag")]
    [InlineData("tr-a")]
    [InlineData("tr tr")]
    [InlineData("tr.tr")]
    [InlineData("1r")]
    [InlineData("*")]
    public void RejectsMalformedTags(string tag) =>
        LocaleTag.IsWellFormed(tag).Should().BeFalse();

    [Fact]
    public void RejectsATagLongerThanTheMaximum() =>
        LocaleTag.IsWellFormed("tr-" + new string('a', LocaleTag.MaxLength)).Should().BeFalse();

    [Fact]
    public void RejectsUnnormalizedInputEvenWhenItsNormalFormIsValid()
    {
        LocaleTag.IsWellFormed("tr_TR").Should().BeFalse();
        LocaleTag.IsWellFormed(LocaleTag.Normalize("tr_TR")).Should().BeTrue();
    }
}
