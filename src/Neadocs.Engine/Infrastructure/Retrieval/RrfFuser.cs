namespace Neadocs.Engine.Infrastructure.Retrieval;

using System;
using System.Collections.Generic;
using System.Linq;

public sealed record RankedChunk(Guid ChunkId, int Ordinal, double Score);

public sealed record FusedChunk(
    Guid ChunkId,
    double Score,
    int? LexicalRank,
    int? VectorRank,
    double? LexicalScore,
    double? VectorScore);

public static class RrfFuser
{
    public static List<FusedChunk> Fuse(
        IReadOnlyList<RankedChunk> lexical,
        IReadOnlyList<RankedChunk> vector,
        int k)
    {
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, "RrfK must be greater than 0.");
        }

        Dictionary<Guid, Accumulator> byChunk = [];

        Absorb(byChunk, lexical, isLexical: true, k);
        Absorb(byChunk, vector, isLexical: false, k);

        return [.. byChunk.Values
            .Select(a => new FusedChunk(
                a.ChunkId, a.Score, a.LexicalRank, a.VectorRank, a.LexicalScore, a.VectorScore))
            .OrderByDescending(f => f.Score)
            .ThenBy(f => byChunk[f.ChunkId].Ordinal)
            .ThenBy(f => f.ChunkId)];
    }

    private static void Absorb(
        Dictionary<Guid, Accumulator> byChunk,
        IReadOnlyList<RankedChunk> ranked,
        bool isLexical,
        int k)
    {
        for (int i = 0; i < ranked.Count; i++)
        {
            RankedChunk chunk = ranked[i];
            int rank = i + 1;

            if (!byChunk.TryGetValue(chunk.ChunkId, out Accumulator? accumulator))
            {
                accumulator = new Accumulator { ChunkId = chunk.ChunkId, Ordinal = chunk.Ordinal };
                byChunk[chunk.ChunkId] = accumulator;
            }

            accumulator.Score += 1.0 / (k + rank);

            if (isLexical)
            {
                accumulator.LexicalRank = rank;
                accumulator.LexicalScore = chunk.Score;
            }
            else
            {
                accumulator.VectorRank = rank;
                accumulator.VectorScore = chunk.Score;
            }
        }
    }

    private sealed class Accumulator
    {
        public Guid ChunkId { get; init; }

        public int Ordinal { get; init; }

        public double Score { get; set; }

        public int? LexicalRank { get; set; }

        public int? VectorRank { get; set; }

        public double? LexicalScore { get; set; }

        public double? VectorScore { get; set; }
    }
}
