namespace Neadocs.Engine.Infrastructure.Evaluation;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Features;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Diagnostics;
using Neadocs.Engine.Infrastructure.Retrieval;

public sealed class EvalCase
{
    public string Query { get; set; } = string.Empty;

    public List<string> Expect { get; set; } = [];

    public int MaxRank { get; set; } = 3;
}

public sealed class EvalSet
{
    public string Collection { get; set; } = string.Empty;

    public string Locale { get; set; } = string.Empty;

    public string? Mode { get; set; }

    public List<EvalCase> Cases { get; set; } = [];
}

public sealed class EvalCaseResult
{
    public string Query { get; set; } = string.Empty;

    public List<string> Expect { get; set; } = [];

    public int MaxRank { get; set; }

    public int ActualRank { get; set; }

    public bool Passed { get; set; }

    public long TookMs { get; set; }

    public List<string> Returned { get; set; } = [];
}

public sealed class EvalReport
{
    public string Collection { get; set; } = string.Empty;

    public string Locale { get; set; } = string.Empty;

    public string Mode { get; set; } = string.Empty;

    public int Total { get; set; }

    public int Passed { get; set; }

    public double RecallAt1 { get; set; }

    public double RecallAt3 { get; set; }

    public double RecallAt10 { get; set; }

    public double Mrr { get; set; }

    public double MeanLatencyMs { get; set; }

    public double MinRecallAt3 { get; set; }

    public bool Meets { get; set; }

    public List<string> Failures { get; set; } = [];

    public List<EvalCaseResult> Cases { get; set; } = [];
}

public sealed class EvalRunner
{
    private readonly SearchService _search;
    private readonly DocumentEngineOptions _options;

    public EvalRunner(SearchService search, IOptions<DocumentEngineOptions> options)
    {
        _search = search;
        _options = options.Value;
    }

    public async Task<EvalReport?> RunAsync(string tenant, EvalSet set, CancellationToken ct)
    {
        string mode = string.IsNullOrWhiteSpace(set.Mode) ? _options.DefaultSearchMode : set.Mode;

        EvalReport report = new()
        {
            Collection = set.Collection,
            Locale = set.Locale,
            Mode = mode,
            Total = set.Cases.Count,
            MinRecallAt3 = _options.MinRecallAt3,
        };

        int hitsAt1 = 0;
        int hitsAt3 = 0;
        int hitsAt10 = 0;
        double reciprocalSum = 0;
        long latencySum = 0;

        foreach (EvalCase testCase in set.Cases)
        {
            long started = Stopwatch.GetTimestamp();

            SearchResponse? response = await _search.SearchAsync(
                tenant,
                set.Collection,
                new SearchRequest { Query = testCase.Query, Locale = set.Locale, Limit = 10 },
                mode,
                ct);

            if (response is null)
            {
                return null;
            }

            long took = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            latencySum += took;

            List<string> returned = [.. response.Hits.Select(h => h.ExternalKey).Distinct()];
            int rank = RankOf(returned, testCase.Expect);

            if (rank == 1)
            {
                hitsAt1++;
            }

            if (rank is > 0 and <= 3)
            {
                hitsAt3++;
            }

            if (rank is > 0 and <= 10)
            {
                hitsAt10++;
            }

            if (rank > 0)
            {
                reciprocalSum += 1.0 / rank;
            }

            bool passed = rank > 0 && rank <= testCase.MaxRank;

            if (passed)
            {
                report.Passed++;
            }
            else
            {
                report.Failures.Add(
                    $"'{testCase.Query}' expected {string.Join(" or ", testCase.Expect)} within rank "
                    + $"{testCase.MaxRank}, got "
                    + (rank > 0 ? $"rank {rank}" : "no match")
                    + (returned.Count > 0 ? $" (returned: {string.Join(", ", returned.Take(3))})" : " (no hits)"));
            }

            report.Cases.Add(new EvalCaseResult
            {
                Query = testCase.Query,
                Expect = testCase.Expect,
                MaxRank = testCase.MaxRank,
                ActualRank = rank,
                Passed = passed,
                TookMs = took,
                Returned = [.. returned.Take(5)],
            });
        }

        int total = Math.Max(1, set.Cases.Count);

        report.RecallAt1 = (double)hitsAt1 / total;
        report.RecallAt3 = (double)hitsAt3 / total;
        report.RecallAt10 = (double)hitsAt10 / total;
        report.Mrr = reciprocalSum / total;
        report.MeanLatencyMs = (double)latencySum / total;

        bool rankOneCasesHold = report.Cases.TrueForAll(c => c.MaxRank != 1 || c.Passed);

        report.Meets = report.RecallAt3 >= _options.MinRecallAt3 && rankOneCasesHold;

        NeadocsMeters.SetRecallAt3(set.Collection, set.Locale, report.RecallAt3);

        return report;
    }

    private static int RankOf(List<string> returned, List<string> expected)
    {
        for (int i = 0; i < returned.Count; i++)
        {
            if (expected.Contains(returned[i], StringComparer.Ordinal))
            {
                return i + 1;
            }
        }

        return 0;
    }
}
