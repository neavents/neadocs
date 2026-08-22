namespace Neadocs.Engine.Tests.Unit.Diagnostics;

using System;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Neadocs.Engine.Infrastructure.Diagnostics;

public sealed class CorrelationIdTests
{
    [Theory]
    [InlineData("abc123")]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e4736")]
    [InlineData("req-1234")]
    [InlineData("req_1234")]
    [InlineData("service.v1:42")]
    [InlineData("A")]
    public void AcceptsAWellFormedInboundId(string candidate) =>
        CorrelationId.IsWellFormed(candidate).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("new\nline")]
    [InlineData("carriage\rreturn")]
    [InlineData("tab\there")]
    [InlineData("semi;colon")]
    [InlineData("quote\"here")]
    [InlineData("<script>")]
    [InlineData("türkçe")]
    [InlineData("emoji-\U0001F600")]
    public void RejectsAnIdThatCouldForgeALogLineOrAHeader(string? candidate) =>
        CorrelationId.IsWellFormed(candidate).Should().BeFalse();

    [Fact]
    public void RejectsAnOverlongId() =>
        CorrelationId.IsWellFormed(new string('a', CorrelationId.MaxLength + 1)).Should().BeFalse();

    [Fact]
    public void AcceptsAnIdAtExactlyTheMaximum() =>
        CorrelationId.IsWellFormed(new string('a', CorrelationId.MaxLength)).Should().BeTrue();

    [Fact]
    public void GeneratesAWellFormedIdWithNoAmbientActivity()
    {
        Activity.Current = null;

        string id = CorrelationId.Generate();

        CorrelationId.IsWellFormed(id).Should().BeTrue();
        id.Should().HaveLength(32);
    }

    [Fact]
    public void GeneratedIdsAreDistinct()
    {
        Activity.Current = null;

        CorrelationId.Generate().Should().NotBe(CorrelationId.Generate());
    }

    [Fact]
    public void PrefersTheAmbientTraceIdSoLogsAndTracesJoinUp()
    {
        using ActivityListener listener = new()
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using ActivitySource source = new("Neadocs.Tests.Correlation");
        using Activity? activity = source.StartActivity("request");

        activity.Should().NotBeNull();

        string id = CorrelationId.Generate();

        id.Should().Be(activity!.TraceId.ToHexString());
        CorrelationId.IsWellFormed(id).Should().BeTrue();
    }

    [Fact]
    public void ReadsBackAnIdStoredOnTheContext()
    {
        DefaultHttpContext context = new();
        context.Items[CorrelationId.ItemKey] = "abc";

        CorrelationId.Of(context).Should().Be("abc");
    }

    [Fact]
    public void ReturnsEmptyWhenNoIdIsStored() =>
        CorrelationId.Of(new DefaultHttpContext()).Should().BeEmpty();

    [Fact]
    public void ReturnsEmptyWhenTheStoredValueIsNotAString()
    {
        DefaultHttpContext context = new();
        context.Items[CorrelationId.ItemKey] = 42;

        CorrelationId.Of(context).Should().BeEmpty();
    }

    [Fact]
    public void TheHeaderNameIsTheEstateConvention() =>
        CorrelationId.HeaderName.Should().Be("X-Correlation-Id");
}
