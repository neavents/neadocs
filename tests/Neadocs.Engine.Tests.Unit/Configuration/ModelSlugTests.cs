namespace Neadocs.Engine.Tests.Unit.Configuration;

using FluentAssertions;
using Neadocs.Engine.Infrastructure.Configuration;

public sealed class ModelSlugTests
{
    [Theory]
    [InlineData("gemini-embedding-001", "gemini_embedding_001")]
    [InlineData("text-embedding-3-small", "text_embedding_3_small")]
    [InlineData("text-embedding-3-large", "text_embedding_3_large")]
    [InlineData("deepseek-embed", "deepseek_embed")]
    public void MapsRealModelNames(string model, string expected) =>
        ModelSlug.From(model).Should().Be(expected);

    [Theory]
    [InlineData("Gemini-Embedding-001", "gemini_embedding_001")]
    [InlineData("TEXT-EMBEDDING-3-SMALL", "text_embedding_3_small")]
    [InlineData("MiXeD", "mixed")]
    public void LowercasesAsciiWithoutCulture(string model, string expected) =>
        ModelSlug.From(model).Should().Be(expected);

    [Theory]
    [InlineData("a---b", "a_b")]
    [InlineData("a...b", "a_b")]
    [InlineData("a / b", "a_b")]
    [InlineData("a@@@@@@b", "a_b")]
    public void CollapsesRunsOfSeparators(string model, string expected) =>
        ModelSlug.From(model).Should().Be(expected);

    [Theory]
    [InlineData("--foo--", "foo")]
    [InlineData("///foo///", "foo")]
    [InlineData("_foo_", "foo")]
    [InlineData(" foo ", "foo")]
    public void TrimsLeadingAndTrailingSeparators(string model, string expected) =>
        ModelSlug.From(model).Should().Be(expected);

    [Fact]
    public void TruncatesToMaxLength()
    {
        string slug = ModelSlug.From(new string('a', 100));

        slug.Should().HaveLength(ModelSlug.MaxLength);
        slug.Should().Be(new string('a', ModelSlug.MaxLength));
    }

    [Fact]
    public void TruncationNeverLeavesATrailingSeparator()
    {
        string slug = ModelSlug.From(new string('a', ModelSlug.MaxLength) + "-tail");

        slug.Should().Be(new string('a', ModelSlug.MaxLength));
        slug.Should().NotEndWith("_");
    }

    [Fact]
    public void TruncationTrimsTheSeparatorLandingExactlyOnTheBoundary()
    {
        string model = new string('a', ModelSlug.MaxLength - 1) + "-b";

        string slug = ModelSlug.From(model);

        slug.Should().Be(new string('a', ModelSlug.MaxLength - 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("---")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void YieldsEmptyWhenNothingSurvives(string model) =>
        ModelSlug.From(model).Should().BeEmpty();

    [Theory]
    [InlineData("IST", "ist")]
    [InlineData("İSTANBUL", "stanbul")]
    [InlineData("Menü", "men")]
    [InlineData("Café", "caf")]
    public void FoldsOnlyAsciiAndDropsTheRest(string model, string expected) =>
        ModelSlug.From(model).Should().Be(expected);

    [Fact]
    public void IsDeterministicAcrossRepeatedCalls()
    {
        const string model = "Gemini-Embedding-001";

        ModelSlug.From(model).Should().Be(ModelSlug.From(model));
    }

    [Fact]
    public void ProducesOnlySlugSafeCharacters()
    {
        string slug = ModelSlug.From("Ünïcödé Mödèl/v2.5 (beta) — 001");

        slug.Should().MatchRegex("^[a-z0-9_]+$");
        slug.Should().NotStartWith("_").And.NotEndWith("_");
    }

    [Theory]
    [InlineData("gemini_embedding_001", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("_leading", false)]
    [InlineData("trailing_", false)]
    public void ValidatesSlugShape(string slug, bool expected) =>
        ModelSlug.IsValid(slug).Should().Be(expected);

    [Fact]
    public void RejectsAnOverlongSlug() =>
        ModelSlug.IsValid(new string('a', ModelSlug.MaxLength + 1)).Should().BeFalse();

    [Fact]
    public void DistinctModelNamesCanCollideAndTheValidatorMustCatchIt()
    {
        ModelSlug.From("gemini-embedding-001").Should().Be(ModelSlug.From("gemini.embedding.001"));
    }
}
