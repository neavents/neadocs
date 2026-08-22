namespace Neadocs.Engine.Infrastructure.Diagnostics;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

public static class NeadocsMeters
{
    public const string MeterName = "Neadocs.Engine";

    private const char ScopeSeparator = (char)0x1F;

    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> DocumentsUpserted =
        Meter.CreateCounter<long>("neadocs.documents.upserted");

    public static readonly Counter<long> ChunksCreated =
        Meter.CreateCounter<long>("neadocs.chunks.created");

    public static readonly Counter<long> ChunksDeleted =
        Meter.CreateCounter<long>("neadocs.chunks.deleted");

    public static readonly Counter<long> EmbeddingsComputed =
        Meter.CreateCounter<long>("neadocs.embeddings.computed");

    public static readonly Counter<long> EmbeddingCacheHits =
        Meter.CreateCounter<long>("neadocs.embeddings.cache_hits");

    public static readonly Counter<long> EmbeddingTokens =
        Meter.CreateCounter<long>("neadocs.embeddings.tokens");

    public static readonly Counter<double> EmbeddingCostUsd =
        Meter.CreateCounter<double>("neadocs.embeddings.cost_usd");

    public static readonly Counter<long> ProviderFailures =
        Meter.CreateCounter<long>("neadocs.provider.failures");

    public static readonly Histogram<double> SearchDuration =
        Meter.CreateHistogram<double>("neadocs.search.duration", "ms");

    public static readonly Histogram<int> SearchHits =
        Meter.CreateHistogram<int>("neadocs.search.hits");

    private static readonly ConcurrentDictionary<string, long> BacklogDepthByModel = new();
    private static readonly ConcurrentDictionary<string, int> CircuitOpenByProvider = new();
    private static readonly ConcurrentDictionary<string, double> RecallAt3ByScope = new();

    public static readonly ObservableGauge<long> EmbeddingBacklogDepth =
        Meter.CreateObservableGauge("neadocs.embeddings.backlog_depth", ObserveBacklogDepth);

    public static readonly ObservableGauge<int> ProviderCircuitOpen =
        Meter.CreateObservableGauge("neadocs.provider.circuit_open", ObserveCircuitOpen);

    public static readonly ObservableGauge<double> EvalRecallAt3 =
        Meter.CreateObservableGauge("neadocs.eval.recall_at_3", ObserveRecallAt3);

    private static string _buildVersion = "unknown";
    private static string _buildSchema = "unknown";

    public static readonly ObservableGauge<int> BuildInfo =
        Meter.CreateObservableGauge("neadocs.build.info", ObserveBuildInfo);

    public static void SetBuildInfo(string version, string schema)
    {
        _buildVersion = version;
        _buildSchema = schema;
    }

    private static IEnumerable<Measurement<int>> ObserveBuildInfo()
    {
        yield return new Measurement<int>(
            1,
            new KeyValuePair<string, object?>("neadocs.version", _buildVersion),
            new KeyValuePair<string, object?>(NeadocsTags.Schema, _buildSchema));
    }

    public static void SetBacklogDepth(string modelSlug, long depth) =>
        BacklogDepthByModel[modelSlug] = depth;

    public static void SetCircuitOpen(string provider, bool open) =>
        CircuitOpenByProvider[provider] = open ? 1 : 0;

    public static void SetRecallAt3(string collection, string locale, double recall) =>
        RecallAt3ByScope[collection + ScopeSeparator + locale] = recall;

    internal static void ResetObservableState()
    {
        BacklogDepthByModel.Clear();
        CircuitOpenByProvider.Clear();
        RecallAt3ByScope.Clear();
    }

    private static IEnumerable<Measurement<long>> ObserveBacklogDepth()
    {
        foreach (KeyValuePair<string, long> entry in BacklogDepthByModel)
        {
            yield return new Measurement<long>(
                entry.Value,
                new KeyValuePair<string, object?>(NeadocsTags.Model, entry.Key));
        }
    }

    private static IEnumerable<Measurement<int>> ObserveCircuitOpen()
    {
        foreach (KeyValuePair<string, int> entry in CircuitOpenByProvider)
        {
            yield return new Measurement<int>(
                entry.Value,
                new KeyValuePair<string, object?>(NeadocsTags.Provider, entry.Key));
        }
    }

    private static IEnumerable<Measurement<double>> ObserveRecallAt3()
    {
        foreach (KeyValuePair<string, double> entry in RecallAt3ByScope)
        {
            string[] parts = entry.Key.Split(ScopeSeparator);

            yield return new Measurement<double>(
                entry.Value,
                new KeyValuePair<string, object?>(NeadocsTags.Collection, parts[0]),
                new KeyValuePair<string, object?>(NeadocsTags.Locale, parts[1]));
        }
    }
}
