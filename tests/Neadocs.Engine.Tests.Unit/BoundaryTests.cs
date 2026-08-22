namespace Neadocs.Engine.Tests.Unit;

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Chunking;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Text;

public sealed class LocaleTagBoundaryTests
{
    [Fact]
    public void ATagOfExactlyTheMaximumLengthIsAccepted()
    {
        const string tag = "en-aaaaaaaa-bbbbbbbb-cccccccc-ddddd";

        tag.Should().HaveLength(LocaleTag.MaxLength);
        LocaleTag.IsWellFormed(tag).Should().BeTrue(
            "the limit is inclusive; an off-by-one here silently rejects a legal tag");
    }

    [Fact]
    public void ATagOneCharacterOverTheMaximumIsRejected() =>
        LocaleTag.IsWellFormed("en-" + new string('a', LocaleTag.MaxLength)).Should().BeFalse();

    [Fact]
    public void ASubtagOfExactlyEightCharactersIsAccepted() =>
        LocaleTag.IsWellFormed("en-" + new string('a', 8)).Should().BeTrue();

    [Fact]
    public void ASubtagOfNineCharactersIsRejected() =>
        LocaleTag.IsWellFormed("en-" + new string('a', 9)).Should().BeFalse();

    [Fact]
    public void ASubtagOfExactlyTwoCharactersIsAccepted() =>
        LocaleTag.IsWellFormed("en-gb").Should().BeTrue();

    [Fact]
    public void ASubtagOfOneCharacterIsRejected() =>
        LocaleTag.IsWellFormed("en-g").Should().BeFalse();

    [Theory]
    [InlineData("aa")]
    [InlineData("zz")]
    [InlineData("az")]
    public void TheFirstSubtagAcceptsTheFullLowercaseRange(string tag) =>
        LocaleTag.IsWellFormed(tag).Should().BeTrue();

    [Theory]
    [InlineData("en-a0")]
    [InlineData("en-a9")]
    [InlineData("en-az")]
    public void ALaterSubtagAcceptsTheFullLetterAndDigitRanges(string tag) =>
        LocaleTag.IsWellFormed(tag).Should().BeTrue();

    [Theory]
    [InlineData("en-a/")]
    [InlineData("en-a:")]
    [InlineData("en-a`")]
    [InlineData("en-a{")]
    public void ALaterSubtagRejectsTheCharactersJustOutsideThoseRanges(string tag) =>
        LocaleTag.IsWellFormed(tag).Should().BeFalse();

    [Theory]
    [InlineData("a`")]
    [InlineData("a{")]
    public void TheFirstSubtagRejectsTheCharactersJustOutsideTheLetterRange(string tag) =>
        LocaleTag.IsWellFormed(tag).Should().BeFalse();
}

public sealed class ValidatorBoundaryTests
{
    private static DocumentEngineOptions Valid() => new()
    {
        PostgresConnectionString = "Host=localhost;Database=d;Username=u;Password=p",
        Schema = "neadocs",
        JwtSymmetricKey = new string('k', 32),
        Text = new TextOptions
        {
            Locales = ["tr", "en"],
            DefaultLocale = "tr",
            LocaleFallback = new Dictionary<string, List<string>> { ["tr"] = ["en"] },
        },
    };

    private static IReadOnlyList<string> Errors(System.Action<DocumentEngineOptions> mutate)
    {
        DocumentEngineOptions options = Valid();
        mutate(options);

        return DocumentEngineOptionsValidator.Validate(options);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void MinRecallAcceptsBothEndsOfItsRange(double value) =>
        Errors(o => o.MinRecallAt3 = value).Should().BeEmpty();

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public void OverlapPercentAcceptsBothEndsOfItsRange(int value) =>
        Errors(o => o.Chunking.OverlapPercent = value).Should().BeEmpty();

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void SplitAtHeadingLevelAcceptsBothEndsOfItsRange(int value) =>
        Errors(o => o.Chunking.SplitAtHeadingLevel = value).Should().BeEmpty();

    [Theory]
    [InlineData(50)]
    [InlineData(4000)]
    public void TargetTokensAcceptsBothEndsOfItsRange(int value) =>
        Errors(o => o.Chunking.TargetTokens = value).Should().BeEmpty();

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void TrigramThresholdAcceptsBothEndsOfItsRange(double value) =>
        Errors(o => o.Text.TrigramThreshold = value).Should().BeEmpty();

    [Fact]
    public void ASynonymGroupOfExactlyTwoTermsIsAccepted() =>
        Errors(o => o.Text.Synonyms["tr"] = [new() { Terms = ["a", "b"] }]).Should().BeEmpty();

    [Fact]
    public void ASynonymGroupOfThreeTermsIsAccepted() =>
        Errors(o => o.Text.Synonyms["tr"] = [new() { Terms = ["a", "b", "c"] }]).Should().BeEmpty();

    [Fact]
    public void ACircuitBreakerRatioOfExactlyOneIsAccepted() =>
        Errors(o => o.Resilience.CircuitBreakerFailureRatio = 1.0).Should().BeEmpty();

    [Fact]
    public void AMinimumThroughputOfExactlyTwoIsAccepted() =>
        Errors(o => o.Resilience.CircuitBreakerMinimumThroughput = 2).Should().BeEmpty();

    [Fact]
    public void ZeroRetriesIsAccepted() =>
        Errors(o => o.Resilience.MaxRetries = 0).Should().BeEmpty();

    [Fact]
    public void AZeroQueueSizeIsAccepted() =>
        Errors(o => o.RateLimitQueueSize = 0).Should().BeEmpty();

    [Fact]
    public void ASchemaOfExactlyTheMaximumLengthIsAccepted() =>
        Errors(o => o.Schema = new string('a', Neadocs.Engine.Infrastructure.Storage.SqlIdentifier.MaxLength))
            .Should().BeEmpty();

    [Fact]
    public void TheSchemaErrorQuotesTheRealUpperBound()
    {
        string error = Errors(o => o.Schema = "BAD")[0];

        error.Should().Contain(
            (Neadocs.Engine.Infrastructure.Storage.SqlIdentifier.MaxLength - 1)
                .ToString(CultureInfo.InvariantCulture),
            "the message quotes the pattern an operator will paste into a check; an off-by-one "
            + "here sends them chasing a bound that does not exist");
    }

    [Fact]
    public void EveryErrorIsSeparatedOnItsOwnLine()
    {
        DocumentEngineOptions options = new();

        System.Action act = () => DocumentEngineOptionsValidator.ThrowIfInvalid(options);

        string message = act.Should().Throw<System.InvalidOperationException>().Which.Message;
        int errorCount = DocumentEngineOptionsValidator.Validate(options).Count;

        message.Split("\n  - ").Should().HaveCount(errorCount + 1,
            "errors run together on one line are unreadable at a container boot");
    }

    [Fact]
    public void ASingleErrorIsReportedInTheSingular()
    {
        DocumentEngineOptions options = Valid();
        options.RrfK = 0;

        System.Action act = () => DocumentEngineOptionsValidator.ThrowIfInvalid(options);

        act.Should().Throw<System.InvalidOperationException>()
            .Which.Message.Should().Contain("1 configuration error:").And.NotContain("errors:");
    }

    [Fact]
    public void AFallbackCycleIsReportedOnceNotOncePerNode()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.Text.Locales = ["tr", "en", "de"];
            o.Text.LocaleFallback = new Dictionary<string, List<string>>
            {
                ["tr"] = ["en"],
                ["en"] = ["de"],
                ["de"] = ["tr"],
            };
        });

        errors.Count(e => e.Contains("cycle")).Should().Be(1);
    }

    [Fact]
    public void AnUnknownFallbackTargetDoesNotMaskTheRemainingTargets()
    {
        IReadOnlyList<string> errors = Errors(o =>
            o.Text.LocaleFallback["tr"] = ["de", "fr"]);

        errors.Should().HaveCount(2, "each bad target is named, not just the first");
    }

    [Fact]
    public void AnInvalidLocaleDoesNotStopLaterLocalesBeingChecked()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.Text.Locales = ["tr", "!!bad!!", "??worse??"];
            o.Text.LocaleFallback = [];
        });

        errors.Count(e => e.Contains("Text:Locales contains")).Should().Be(2);
    }

    [Fact]
    public void AModelWithNoNameDoesNotStopLaterModelsBeingChecked()
    {
        IReadOnlyList<string> errors = Errors(o =>
        {
            o.EmbeddingModels =
            [
                new() { Provider = "gemini", Dimensions = 8 },
                new() { Provider = "gemini", Dimensions = 8 },
            ];
        });

        errors.Count(e => e.Contains("Model must be set")).Should().Be(2);
    }
}

public sealed class ChunkerBoundaryTests
{
    private static MarkdownChunker Chunker(int targetTokens = 400, int overlapPercent = 15) =>
        new(new ChunkingOptions
        {
            TargetTokens = targetTokens,
            OverlapPercent = overlapPercent,
            SplitAtHeadingLevel = 2,
            CharsPerToken = 1,
            MaxChunksPerDocument = 500,
        });

    [Fact]
    public void ABlockThatExactlyReachesTheTargetStaysInTheCurrentChunk()
    {
        string a = new('a', 10);
        string b = new('b', 10);
        string content = $"# T\n\n## S\n\n{a}\n\n{b}";

        IReadOnlyList<DocumentChunk> chunks = Chunker(targetTokens: 24, overlapPercent: 0).Chunk(content);

        chunks.Should().ContainSingle(
            "10 + 2 + 10 = 22 characters is under the 24 token target, so the second block joins "
            + "the first. A >= comparison here would split one block too early.");
    }

    [Fact]
    public void ABlockThatExceedsTheTargetStartsANewChunk()
    {
        string a = new('a', 10);
        string b = new('b', 10);
        string content = $"# T\n\n## S\n\n{a}\n\n{b}";

        IReadOnlyList<DocumentChunk> chunks = Chunker(targetTokens: 21, overlapPercent: 0).Chunk(content);

        chunks.Should().HaveCount(2);
    }

    [Fact]
    public void AZeroOverlapPercentProducesNoOverlapEvenWhenOneWouldFit()
    {
        string body = string.Join(" ", Enumerable.Repeat("Bir cumle burada.", 30));
        string content = "# T\n\n## S\n\n" + string.Join("\n\n", Enumerable.Repeat(body, 4));

        IReadOnlyList<DocumentChunk> withOverlap =
            Chunker(targetTokens: 200, overlapPercent: 20).Chunk(content);
        IReadOnlyList<DocumentChunk> withoutOverlap =
            Chunker(targetTokens: 200, overlapPercent: 0).Chunk(content);

        withOverlap.Skip(1).Should().Contain(c => c.Overlap.Length > 0);
        withoutOverlap.Should().OnlyContain(c => c.Overlap.Length == 0);
    }

    [Fact]
    public void OverlapIsEmptyWhenThePreviousChunkIsEmpty() =>
        Chunker(overlapPercent: 50).TrailingOverlap("").Should().BeEmpty();

    [Fact]
    public void OverlapStartsAfterTheSentenceTerminatorAndItsSpace()
    {
        string overlap = Chunker(overlapPercent: 40)
            .TrailingOverlap("First sentence. Second sentence. Third sentence.");

        overlap.Should().StartWith("Third").And.NotStartWith(".").And.NotStartWith(" ");
    }

    [Fact]
    public void ATerminatorAtTheVeryEndDoesNotProduceAnEmptyOverlap()
    {
        string overlap = Chunker(overlapPercent: 30).TrailingOverlap("One two three four five.");

        overlap.Should().NotBeNull();
        overlap.Should().NotStartWith(" ");
    }

    [Theory]
    [InlineData("!")]
    [InlineData("?")]
    [InlineData("…")]
    public void EverySentenceTerminatorIsRecognised(string terminator)
    {
        string overlap = Chunker(overlapPercent: 40)
            .TrailingOverlap($"First one{terminator} Second one{terminator} Third one{terminator}");

        overlap.Should().StartWith("Third");
    }

    [Fact]
    public void TokenEstimationRoundsUpRatherThanTruncating()
    {
        MarkdownChunker chunker = new(new ChunkingOptions
        {
            TargetTokens = 400,
            OverlapPercent = 0,
            SplitAtHeadingLevel = 2,
            CharsPerToken = 3.5,
            MaxChunksPerDocument = 500,
        });

        chunker.EstimateTokens(new string('a', 1)).Should().Be(1);
        chunker.EstimateTokens(new string('a', 4)).Should().Be(2);
        chunker.EstimateTokens(new string('a', 7)).Should().Be(2);
        chunker.EstimateTokens(new string('a', 8)).Should().Be(3);
    }

    [Fact]
    public void TheChunkCeilingIsInclusive()
    {
        string content = "# T\n\n" + string.Join("\n\n",
            Enumerable.Range(0, 20).Select(i => $"## H{i}\n\nBody {i}."));

        MarkdownChunker chunker = new(new ChunkingOptions
        {
            TargetTokens = 400,
            OverlapPercent = 0,
            SplitAtHeadingLevel = 2,
            CharsPerToken = 3.5,
            MaxChunksPerDocument = 3,
        });

        chunker.Chunk(content).Should().HaveCount(3);
    }
}

public sealed class OperationBoundaryTests
{
    [Fact]
    public void TheAsciiFastPathAndTheWideMapAgreeAtTheBoundary()
    {
        MapCharsOperation op = new(new Dictionary<char, string>
        {
            [''] = "L",
            [''] = "H",
        });

        op.Apply("").Should().Be("LH",
            "127 is the last ASCII slot and 128 the first wide one; an inclusive comparison here "
            + "would index past the lookup array or drop the mapping");
    }

    [Fact]
    public void AnEmptySequenceMapLeavesANonEmptyInputAlone()
    {
        MapSequencesOperation op = new(new Dictionary<string, string>());

        op.Apply("untouched").Should().Be("untouched");
    }

    [Fact]
    public void ASequenceMapLeavesAnEmptyInputAlone()
    {
        MapSequencesOperation op = new(new Dictionary<string, string> { ["ab"] = "X" });

        op.Apply("").Should().BeEmpty();
    }

    [Fact]
    public void ASequenceMatchingTheEntireRemainingInputIsReplaced()
    {
        MapSequencesOperation op = new(new Dictionary<string, string> { ["abc"] = "X" });

        op.Apply("abc").Should().Be("X");
        op.Apply("zabc").Should().Be("zX");
    }

    [Fact]
    public void ASequenceLongerThanTheRemainingInputIsNotMatched()
    {
        MapSequencesOperation op = new(new Dictionary<string, string> { ["abcd"] = "X" });

        op.Apply("abc").Should().Be("abc");
    }
}
