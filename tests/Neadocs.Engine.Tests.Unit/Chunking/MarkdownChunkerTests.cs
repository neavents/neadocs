namespace Neadocs.Engine.Tests.Unit.Chunking;

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Chunking;
using Neadocs.Engine.Infrastructure.Configuration;

public sealed class MarkdownChunkerTests
{
    private static MarkdownChunker Chunker(
        int targetTokens = 400,
        int overlapPercent = 15,
        int splitAtHeadingLevel = 2) =>
        new(new ChunkingOptions
        {
            TargetTokens = targetTokens,
            OverlapPercent = overlapPercent,
            SplitAtHeadingLevel = splitAtHeadingLevel,
            CharsPerToken = 3.5,
            MaxChunksPerDocument = 500,
        });

    private const string Article = """
        # Menus

        Intro paragraph about menus.

        ## Publishing

        First publishing paragraph.

        Second publishing paragraph.

        ## Troubleshooting

        Something went wrong.
        """;

    [Fact]
    public void ReturnsNothingForEmptyContent()
    {
        Chunker().Chunk("").Should().BeEmpty();
        Chunker().Chunk("   ").Should().BeEmpty();
    }

    [Fact]
    public void SplitsAtHeadingsOfTheConfiguredLevel()
    {
        IReadOnlyList<DocumentChunk> chunks = Chunker().Chunk(Article);

        chunks.Should().HaveCount(3);
    }

    [Fact]
    public void RecordsTheHeadingPathAsASequenceOfSegments()
    {
        IReadOnlyList<DocumentChunk> chunks = Chunker().Chunk(Article);

        chunks[0].HeadingPath.Should().Equal(["Menus"]);
        chunks[1].HeadingPath.Should().Equal(["Menus", "Publishing"]);
        chunks[2].HeadingPath.Should().Equal(["Menus", "Troubleshooting"]);
    }

    [Fact]
    public void TheHeadingPathIsNeverAPreJoinedString()
    {
        IReadOnlyList<DocumentChunk> chunks = Chunker().Chunk(Article);

        chunks.Should().OnlyContain(c => c.HeadingPath.All(s => !s.Contains('›')));
    }

    [Fact]
    public void OrdinalsAreSequentialFromZero()
    {
        IReadOnlyList<DocumentChunk> chunks = Chunker().Chunk(Article);

        chunks.Select(c => c.Ordinal).Should().Equal([0, 1, 2]);
    }

    [Fact]
    public void HeadingTextIsNotRepeatedInTheBody()
    {
        IReadOnlyList<DocumentChunk> chunks = Chunker().Chunk(Article);

        chunks[1].Body.Should().NotContain("## Publishing");
    }

    [Fact]
    public void TheHeadingPathIsPrependedForTheSearchVectorOnly()
    {
        DocumentChunk chunk = Chunker().Chunk(Article)[1];

        chunk.TsvSource.Should().StartWith("Menus Publishing");
        chunk.Body.Should().NotStartWith("Menus Publishing");
    }

    [Fact]
    public void IsDeterministicAcrossRuns()
    {
        IReadOnlyList<DocumentChunk> first = Chunker().Chunk(Article);
        IReadOnlyList<DocumentChunk> second = Chunker().Chunk(Article);

        first.Select(c => c.ContentHash).Should().Equal(second.Select(c => c.ContentHash));
    }

    [Fact]
    public void NeverSplitsInsideAFencedCodeBlock()
    {
        string content = "# T\n\n## S\n\n```sql\n" + string.Join("\n", Enumerable.Range(0, 200)
            .Select(i => $"SELECT {i} FROM a_very_long_table_name_to_pad_the_line;")) + "\n```\n";

        IReadOnlyList<DocumentChunk> chunks = Chunker(targetTokens: 60).Chunk(content);

        chunks.Should().ContainSingle(c => c.Body.Contains("```sql"));
        chunks.Count(c => c.Body.Contains("SELECT 0 ")).Should().Be(1);

        DocumentChunk code = chunks.Single(c => c.Body.Contains("```sql"));
        code.Body.Should().Contain("SELECT 199").And.EndWith("```");
    }

    [Fact]
    public void EmitsAnOversizedIndivisibleBlockAloneRatherThanBreakingIt()
    {
        string huge = new string('x', 5000);
        string content = $"# T\n\n## S\n\n{huge}\n";

        IReadOnlyList<DocumentChunk> chunks = Chunker(targetTokens: 60).Chunk(content);

        chunks.Should().ContainSingle(c => c.Body.Contains(huge));
    }

    [Fact]
    public void NeverSplitsInsideATable()
    {
        string rows = string.Join("\n", Enumerable.Range(0, 80).Select(i => $"| r{i} | value {i} |"));
        string content = $"# T\n\n## S\n\n| a | b |\n|---|---|\n{rows}\n";

        IReadOnlyList<DocumentChunk> chunks = Chunker(targetTokens: 60).Chunk(content);

        chunks.Count(c => c.Body.Contains("| r0 |")).Should().Be(1);
        chunks.Single(c => c.Body.Contains("| r0 |")).Body.Should().Contain("| r79 |");
    }

    [Fact]
    public void SplitsOnSizeWhenNoHeadingIntervenes()
    {
        string paragraph = new string('a', 700);
        string content = "# T\n\n## S\n\n" + string.Join("\n\n", Enumerable.Repeat(paragraph, 6));

        IReadOnlyList<DocumentChunk> chunks = Chunker(targetTokens: 400).Chunk(content);

        chunks.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void OverlapIsPrependedToEveryChunkAfterTheFirst()
    {
        string paragraph = string.Join(" ", Enumerable.Repeat("Bir cumle burada.", 40));
        string content = "# T\n\n## S\n\n" + string.Join("\n\n", Enumerable.Repeat(paragraph, 4));

        IReadOnlyList<DocumentChunk> chunks = Chunker(targetTokens: 200, overlapPercent: 20).Chunk(content);

        chunks.Should().HaveCountGreaterThan(1);
        chunks[0].Overlap.Should().BeEmpty();
        chunks.Skip(1).Should().OnlyContain(c => c.Overlap.Length > 0);
    }

    [Fact]
    public void OverlapIsExcludedFromTheContentHash()
    {
        DocumentChunk withOverlap = new(1, ["H"], "the body", "some overlap", 10);
        DocumentChunk withoutOverlap = new(1, ["H"], "the body", "", 10);

        withOverlap.ContentHash.Should().Be(withoutOverlap.ContentHash,
            "a change in chunk n must not invalidate chunk n+1");
    }

    [Fact]
    public void OverlapIsPartOfTheStoredContent()
    {
        DocumentChunk chunk = new(1, ["H"], "the body", "some overlap", 10);

        chunk.Content.Should().Be("some overlap\n\nthe body");
    }

    [Fact]
    public void NoOverlapIsAddedWhenTheSettingIsZero()
    {
        string paragraph = string.Join(" ", Enumerable.Repeat("Bir cumle burada.", 40));
        string content = "# T\n\n## S\n\n" + string.Join("\n\n", Enumerable.Repeat(paragraph, 4));

        IReadOnlyList<DocumentChunk> chunks = Chunker(targetTokens: 200, overlapPercent: 0).Chunk(content);

        chunks.Should().OnlyContain(c => c.Overlap.Length == 0);
    }

    [Fact]
    public void OverlapSnapsToASentenceBoundary()
    {
        MarkdownChunker chunker = Chunker(overlapPercent: 30);

        string overlap = chunker.TrailingOverlap("First sentence here. Second sentence here. Third one.");

        overlap.Should().NotStartWith("ence").And.NotBeEmpty();
        overlap.Should().Be("Third one.");
    }

    [Fact]
    public void OverlapIsEmptyWhenItWouldSwallowTheWholePreviousChunk()
    {
        MarkdownChunker chunker = Chunker(overlapPercent: 50);

        chunker.TrailingOverlap("short").Should().BeEmpty();
    }

    [Fact]
    public void RespectsTheChunkCeiling()
    {
        string content = "# T\n\n" + string.Join("\n\n",
            Enumerable.Range(0, 50).Select(i => $"## H{i}\n\nBody {i}."));

        MarkdownChunker chunker = new(new ChunkingOptions
        {
            TargetTokens = 400,
            OverlapPercent = 0,
            SplitAtHeadingLevel = 2,
            CharsPerToken = 3.5,
            MaxChunksPerDocument = 5,
        });

        chunker.Chunk(content).Should().HaveCountLessThanOrEqualTo(5);
    }

    [Fact]
    public void TokenCountIsAnEstimateFromCharacterLength()
    {
        MarkdownChunker chunker = Chunker();

        chunker.EstimateTokens(new string('a', 350)).Should().Be(100);
        chunker.EstimateTokens("").Should().Be(0);
    }

    [Fact]
    public void ContentWithNoHeadingsStillChunks()
    {
        IReadOnlyList<DocumentChunk> chunks = Chunker().Chunk("Just a paragraph.\n\nAnd another.");

        chunks.Should().ContainSingle();
        chunks[0].HeadingPath.Should().BeEmpty();
    }

    [Fact]
    public void DeeperHeadingsDoNotForceASplitBelowTheConfiguredLevel()
    {
        string content = "# T\n\nIntro.\n\n### Deep\n\nDeep body.";

        IReadOnlyList<DocumentChunk> chunks = Chunker(splitAtHeadingLevel: 2).Chunk(content);

        chunks.Should().ContainSingle();
        chunks[0].HeadingPath.Should().Equal(["T"],
            "the path is the stack as of the chunk's first block");
        chunks[0].Body.Should().Contain("### Deep",
            "a heading that does not start a new chunk stays in the body, or its words would be "
            + "searchable nowhere at all");
    }

    [Fact]
    public void TurkishContentSurvivesChunkingUnchanged()
    {
        const string content = "# Menüler\n\n## Yayınlama\n\nMenüyü yayınlamak için şu adımları izleyin.";

        IReadOnlyList<DocumentChunk> chunks = Chunker().Chunk(content);

        chunks[^1].HeadingPath.Should().Equal(["Menüler", "Yayınlama"]);
        chunks[^1].Body.Should().Contain("yayınlamak");
    }

    [Fact]
    public void RtlContentSurvivesChunkingInLogicalOrder()
    {
        const string content = "# القائمة\n\n## النشر\n\nلنشر القائمة اتبع الخطوات.";

        IReadOnlyList<DocumentChunk> chunks = Chunker().Chunk(content);

        chunks[^1].HeadingPath.Should().Equal(["القائمة", "النشر"]);
        chunks[^1].Body[0].Should().Be('ل');
    }

    [Fact]
    public void ChangingOneChunkLeavesTheOthersHashesUntouched()
    {
        IReadOnlyList<DocumentChunk> before = Chunker(overlapPercent: 0).Chunk(Article);

        string edited = Article.Replace("Something went wrong.", "Something else went wrong.");
        IReadOnlyList<DocumentChunk> after = Chunker(overlapPercent: 0).Chunk(edited);

        after[0].ContentHash.Should().Be(before[0].ContentHash);
        after[1].ContentHash.Should().Be(before[1].ContentHash);
        after[2].ContentHash.Should().NotBe(before[2].ContentHash);
    }
}

public sealed class ChunkHashTests
{
    [Fact]
    public void IsStableForTheSameInput() =>
        ChunkHash.Of(["a", "b"], "body").Should().Be(ChunkHash.Of(["a", "b"], "body"));

    [Fact]
    public void ChangesWhenTheBodyChanges() =>
        ChunkHash.Of(["a"], "one").Should().NotBe(ChunkHash.Of(["a"], "two"));

    [Fact]
    public void ChangesWhenTheHeadingPathChanges() =>
        ChunkHash.Of(["a"], "body").Should().NotBe(ChunkHash.Of(["b"], "body"));

    [Fact]
    public void DistinguishesPathSegmentationRatherThanJoiningNaively() =>
        ChunkHash.Of(["a b"], "x").Should().NotBe(ChunkHash.Of(["a", "b"], "x"));

    [Fact]
    public void APathContainingADisplaySeparatorCannotCollide() =>
        ChunkHash.Of(["a › b"], "x").Should().NotBe(ChunkHash.Of(["a", "b"], "x"));

    [Fact]
    public void IsLowercaseHex() =>
        ChunkHash.Of(["a"], "b").Should().MatchRegex("^[0-9a-f]{64}$");

    [Fact]
    public void DocumentHashCombinesTitleAndContent()
    {
        ChunkHash.OfDocument("t", "c").Should().Be(ChunkHash.OfDocument("t", "c"));
        ChunkHash.OfDocument("t", "c").Should().NotBe(ChunkHash.OfDocument("t2", "c"));
        ChunkHash.OfDocument("t", "c").Should().NotBe(ChunkHash.OfDocument("t", "c2"));
    }

    [Fact]
    public void DocumentHashCannotBeFooledByMovingTheBoundary() =>
        ChunkHash.OfDocument("a\nb", "c").Should().NotBe(ChunkHash.OfDocument("a", "b\nc"));
}
