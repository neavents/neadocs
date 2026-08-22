namespace Neadocs.Engine.Tests.Unit.Diagnostics;

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Diagnostics;

public sealed class NeadocsActivitySourcesTests
{
    [Fact]
    public void DeclaresTheFourAreas() =>
        NeadocsActivitySources.All.Should().BeEquivalentTo(
            ["Neadocs.Ingest", "Neadocs.Search", "Neadocs.Provider", "Neadocs.Migration"]);

    [Fact]
    public void EverySourceNameAppearsInTheRegistrationList()
    {
        string[] sources =
        [
            NeadocsActivitySources.Ingest.Name,
            NeadocsActivitySources.Search.Name,
            NeadocsActivitySources.Provider.Name,
            NeadocsActivitySources.Migration.Name,
        ];

        sources.Should().BeEquivalentTo(NeadocsActivitySources.All);
    }

    [Fact]
    public void SourceNamesAreUnique() =>
        NeadocsActivitySources.All.Should().OnlyHaveUniqueItems();

    [Fact]
    public void AListenerSubscribedByNameReceivesSpans()
    {
        List<Activity> started = [];

        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == NeadocsActivitySources.SearchName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = started.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using (Activity? activity = NeadocsActivitySources.Search.StartActivity("search"))
        {
            activity?.SetTag(NeadocsTags.Mode, "hybrid");
        }

        started.Should().ContainSingle();
        started[0].GetTagItem(NeadocsTags.Mode).Should().Be("hybrid");
    }

    [Fact]
    public void EveryTagConstantIsNamespaced() =>
        NeadocsTags.All.Should().OnlyContain(tag => tag.StartsWith("neadocs."));

    [Fact]
    public void TagConstantsAreUnique() =>
        NeadocsTags.All.Should().OnlyHaveUniqueItems();

    [Fact]
    public void TagConstantsAreSnakeCaseAfterTheNamespace() =>
        NeadocsTags.All.Should().OnlyContain(tag => tag.Substring("neadocs.".Length).All(
            c => (c >= 'a' && c <= 'z') || c == '_'));

    [Fact]
    public void NoTagNamesAConsumersDomain()
    {
        string[] forbidden = ["venue", "menu", "org", "smartmenu", "restaurant", "table"];

        foreach (string tag in NeadocsTags.All)
        {
            tag.Should().NotContainAny(forbidden);
        }
    }
}
