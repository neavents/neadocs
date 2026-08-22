namespace Neadocs.Engine.Tests.Unit.Retrieval;

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Retrieval;

public sealed class RrfFuserTests
{
    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static RankedChunk Chunk(int n, double score = 1, int ordinal = 0) =>
        new(Id(n), ordinal, score);

    [Fact]
    public void FusesByRankNotByScore()
    {
        List<FusedChunk> fused = RrfFuser.Fuse(
            [Chunk(1, score: 0.001), Chunk(2, score: 0.0009)],
            [Chunk(2, score: 0.99), Chunk(1, score: 0.98)],
            k: 60);

        fused.Should().HaveCount(2);
        fused[0].Score.Should().BeApproximately(1.0 / 61 + 1.0 / 62, 1e-9,
            "both documents appear at ranks 1 and 2 in the two strategies, so their fused scores tie");
    }

    [Fact]
    public void AChunkRankedFirstByBothStrategiesWins()
    {
        List<FusedChunk> fused = RrfFuser.Fuse(
            [Chunk(1), Chunk(2), Chunk(3)],
            [Chunk(1), Chunk(3), Chunk(2)],
            k: 60);

        fused[0].ChunkId.Should().Be(Id(1));
    }

    [Fact]
    public void RecordsBothRanks()
    {
        List<FusedChunk> fused = RrfFuser.Fuse([Chunk(1), Chunk(2)], [Chunk(2), Chunk(1)], k: 60);

        FusedChunk first = fused.Single(f => f.ChunkId == Id(1));

        first.LexicalRank.Should().Be(1);
        first.VectorRank.Should().Be(2);
    }

    [Fact]
    public void PassesThroughASingleStrategyInOrder()
    {
        List<FusedChunk> fused = RrfFuser.Fuse([Chunk(1), Chunk(2), Chunk(3)], [], k: 60);

        fused.Select(f => f.ChunkId).Should().Equal([Id(1), Id(2), Id(3)]);
        fused.Should().OnlyContain(f => f.VectorRank == null);
    }

    [Fact]
    public void PassesThroughVectorOnlyResults()
    {
        List<FusedChunk> fused = RrfFuser.Fuse([], [Chunk(5), Chunk(6)], k: 60);

        fused.Select(f => f.ChunkId).Should().Equal([Id(5), Id(6)]);
        fused.Should().OnlyContain(f => f.LexicalRank == null);
    }

    [Fact]
    public void ReturnsNothingWhenBothStrategiesAreEmpty() =>
        RrfFuser.Fuse([], [], k: 60).Should().BeEmpty();

    [Fact]
    public void UnionsChunksFoundByOnlyOneStrategy()
    {
        List<FusedChunk> fused = RrfFuser.Fuse([Chunk(1)], [Chunk(2)], k: 60);

        fused.Select(f => f.ChunkId).Should().BeEquivalentTo([Id(1), Id(2)]);
    }

    [Fact]
    public void TiesBreakByOrdinalThenById()
    {
        List<FusedChunk> fused = RrfFuser.Fuse(
            [Chunk(2, ordinal: 5), Chunk(1, ordinal: 1)],
            [Chunk(1, ordinal: 1), Chunk(2, ordinal: 5)],
            k: 60);

        fused[0].ChunkId.Should().Be(Id(1),
            "equal fused scores must resolve deterministically or pagination is unstable");
    }

    [Fact]
    public void IsDeterministicAcrossRepeatedCalls()
    {
        List<FusedChunk> first = RrfFuser.Fuse([Chunk(1), Chunk(2)], [Chunk(3), Chunk(1)], k: 60);
        List<FusedChunk> second = RrfFuser.Fuse([Chunk(1), Chunk(2)], [Chunk(3), Chunk(1)], k: 60);

        first.Select(f => f.ChunkId).Should().Equal(second.Select(f => f.ChunkId));
    }

    [Fact]
    public void AScoreIsAlwaysBetweenZeroAndOne()
    {
        List<FusedChunk> fused = RrfFuser.Fuse([Chunk(1)], [Chunk(1)], k: 60);

        fused[0].Score.Should().BeGreaterThan(0).And.BeLessThan(1);
    }

    [Fact]
    public void ALargerKFlattensTheScoreGap()
    {
        double smallK = RrfFuser.Fuse([Chunk(1), Chunk(2)], [], k: 1)[0].Score
                        - RrfFuser.Fuse([Chunk(1), Chunk(2)], [], k: 1)[1].Score;
        double largeK = RrfFuser.Fuse([Chunk(1), Chunk(2)], [], k: 1000)[0].Score
                        - RrfFuser.Fuse([Chunk(1), Chunk(2)], [], k: 1000)[1].Score;

        largeK.Should().BeLessThan(smallK);
    }

    [Fact]
    public void RefusesANonPositiveK()
    {
        Action act = () => RrfFuser.Fuse([Chunk(1)], [], k: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RequiresNoNormalisationBetweenIncomparableScores()
    {
        List<FusedChunk> fused = RrfFuser.Fuse(
            [Chunk(1, score: 0.0001)],
            [Chunk(2, score: 0.999)],
            k: 60);

        fused[0].Score.Should().Be(fused[1].Score,
            "a ts_rank_cd value and a cosine similarity are not comparable, which is exactly why "
            + "fusion happens on rank");
    }
}
