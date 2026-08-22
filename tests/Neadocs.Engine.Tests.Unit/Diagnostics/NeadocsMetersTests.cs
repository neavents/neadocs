namespace Neadocs.Engine.Tests.Unit.Diagnostics;

using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Diagnostics;

public sealed class NeadocsMetersTests
{
    private static readonly string[] DeclaredInstruments =
    [
        "neadocs.documents.upserted",
        "neadocs.chunks.created",
        "neadocs.chunks.deleted",
        "neadocs.embeddings.computed",
        "neadocs.embeddings.cache_hits",
        "neadocs.embeddings.tokens",
        "neadocs.embeddings.cost_usd",
        "neadocs.embeddings.backlog_depth",
        "neadocs.provider.failures",
        "neadocs.provider.circuit_open",
        "neadocs.search.duration",
        "neadocs.search.hits",
        "neadocs.eval.recall_at_3",
        "neadocs.build.info",
    ];

    private static List<Instrument> PublishedInstruments()
    {
        List<Instrument> published = [];

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == NeadocsMeters.MeterName)
            {
                published.Add(instrument);
            }
        };

        _ = NeadocsMeters.Meter;
        _ = NeadocsMeters.DocumentsUpserted;
        _ = NeadocsMeters.EmbeddingBacklogDepth;
        _ = NeadocsMeters.EvalRecallAt3;
        _ = NeadocsMeters.ProviderCircuitOpen;

        listener.Start();

        return published;
    }

    [Fact]
    public void PublishesEveryDeclaredInstrument()
    {
        IEnumerable<string> names = PublishedInstruments().Select(i => i.Name);

        names.Should().Contain(DeclaredInstruments);
    }

    [Fact]
    public void PublishesNoInstrumentThatIsNotDeclared()
    {
        IEnumerable<string> names = PublishedInstruments().Select(i => i.Name).Distinct();

        names.Should().BeSubsetOf(DeclaredInstruments);
    }

    [Fact]
    public void EveryInstrumentNameIsNamespaced() =>
        PublishedInstruments().Should().OnlyContain(i => i.Name.StartsWith("neadocs."));

    [Fact]
    public void BuildInfoIsAlwaysObservableSoAFreshScrapeProvesTheMeterIsWired()
    {
        List<Measurement<int>> observed = [];

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "neadocs.build.info")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
            observed.Add(new Measurement<int>(value, tags.ToArray())));
        listener.Start();
        listener.RecordObservableInstruments();

        observed.Should().ContainSingle().Which.Value.Should().Be(1);
    }

    [Fact]
    public void BuildInfoCarriesTheVersionAndSchema()
    {
        NeadocsMeters.SetBuildInfo("9.9", "some_schema");

        List<(int Value, string Tag)> observed = ObserveInt("neadocs.build.info", NeadocsTags.Schema);

        observed.Should().ContainSingle().Which.Tag.Should().Be("some_schema");
    }

    [Fact]
    public void TheMeterIsNamedForTheEngineNotForAConsumer() =>
        NeadocsMeters.MeterName.Should().Be("Neadocs.Engine");

    [Fact]
    public void SearchDurationIsReportedInMilliseconds() =>
        PublishedInstruments().Single(i => i.Name == "neadocs.search.duration")
            .Unit.Should().Be("ms");

    [Fact]
    public void BacklogDepthGaugeReportsOneMeasurementPerModel()
    {
        NeadocsMeters.ResetObservableState();
        NeadocsMeters.SetBacklogDepth("gemini_embedding_001", 7);
        NeadocsMeters.SetBacklogDepth("text_embedding_3_small", 0);

        List<(long Value, string Model)> observed = ObserveLong("neadocs.embeddings.backlog_depth", NeadocsTags.Model);

        observed.Should().BeEquivalentTo(
        [
            (7L, "gemini_embedding_001"),
            (0L, "text_embedding_3_small"),
        ]);
    }

    [Fact]
    public void BacklogDepthGaugeOverwritesRatherThanAccumulates()
    {
        NeadocsMeters.ResetObservableState();
        NeadocsMeters.SetBacklogDepth("m", 5);
        NeadocsMeters.SetBacklogDepth("m", 2);

        ObserveLong("neadocs.embeddings.backlog_depth", NeadocsTags.Model)
            .Should().ContainSingle().Which.Value.Should().Be(2);
    }

    [Fact]
    public void CircuitGaugeReportsOneForOpenAndZeroForClosed()
    {
        NeadocsMeters.ResetObservableState();
        NeadocsMeters.SetCircuitOpen("gemini", open: true);
        NeadocsMeters.SetCircuitOpen("openai", open: false);

        List<(int Value, string Provider)> observed = ObserveInt("neadocs.provider.circuit_open", NeadocsTags.Provider);

        observed.Should().BeEquivalentTo([(1, "gemini"), (0, "openai")]);
    }

    [Fact]
    public void RecallGaugeCarriesBothCollectionAndLocale()
    {
        NeadocsMeters.ResetObservableState();
        NeadocsMeters.SetRecallAt3("acme-help", "tr", 0.95);

        List<Measurement<double>> observed = [];

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "neadocs.eval.recall_at_3")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            observed.Add(new Measurement<double>(value, tags.ToArray())));
        listener.Start();
        listener.RecordObservableInstruments();

        observed.Should().ContainSingle();
        observed[0].Value.Should().Be(0.95);
        observed[0].Tags.ToArray().Should().BeEquivalentTo(
        [
            new KeyValuePair<string, object?>(NeadocsTags.Collection, "acme-help"),
            new KeyValuePair<string, object?>(NeadocsTags.Locale, "tr"),
        ]);
    }

    [Fact]
    public void RecallGaugeSurvivesACollectionKeyContainingTheSeparatorlessName()
    {
        NeadocsMeters.ResetObservableState();
        NeadocsMeters.SetRecallAt3("a-b-c", "en-gb", 0.5);

        List<(double Value, string Collection)> observed =
            ObserveDouble("neadocs.eval.recall_at_3", NeadocsTags.Collection);

        observed.Should().ContainSingle().Which.Collection.Should().Be("a-b-c");
    }

    private static List<(long Value, string Tag)> ObserveLong(string instrument, string tagName)
    {
        List<(long, string)> observed = [];

        using MeterListener listener = new();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Name == instrument)
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            observed.Add((value, TagValue(tags, tagName))));
        listener.Start();
        listener.RecordObservableInstruments();

        return observed;
    }

    private static List<(int Value, string Tag)> ObserveInt(string instrument, string tagName)
    {
        List<(int, string)> observed = [];

        using MeterListener listener = new();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Name == instrument)
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
            observed.Add((value, TagValue(tags, tagName))));
        listener.Start();
        listener.RecordObservableInstruments();

        return observed;
    }

    private static List<(double Value, string Tag)> ObserveDouble(string instrument, string tagName)
    {
        List<(double, string)> observed = [];

        using MeterListener listener = new();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Name == instrument)
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            observed.Add((value, TagValue(tags, tagName))));
        listener.Start();
        listener.RecordObservableInstruments();

        return observed;
    }

    private static string TagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == name)
            {
                return tag.Value?.ToString() ?? string.Empty;
            }
        }

        return string.Empty;
    }
}
