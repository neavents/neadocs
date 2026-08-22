namespace Neadocs.Engine.Tests.Unit.Security;

using System;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Neadocs.Engine.Infrastructure.Security;

public sealed class RequestPrincipalTests
{
    [Fact]
    public void ExpandsScopesOnConstructionSoCallersNeverHaveTo()
    {
        RequestPrincipal principal = new("acme", DocumentScope.Admin, RequestPrincipal.JwtMechanism);

        principal.Scopes.Should()
            .Be(DocumentScope.Admin | DocumentScope.Write | DocumentScope.Read);
    }

    [Fact]
    public void GrantsFollowsTheScopeHierarchy()
    {
        RequestPrincipal principal = new("acme", DocumentScope.Write, RequestPrincipal.JwtMechanism);

        principal.Grants(DocumentScope.Read).Should().BeTrue();
        principal.Grants(DocumentScope.Write).Should().BeTrue();
        principal.Grants(DocumentScope.Admin).Should().BeFalse();
    }

    [Fact]
    public void CarriesTheMechanismThatProducedIt()
    {
        RequestPrincipal fromKey = new("acme", DocumentScope.Read, RequestPrincipal.ProjectKeyMechanism);
        RequestPrincipal fromJwt = new("acme", DocumentScope.Read, RequestPrincipal.JwtMechanism);

        fromKey.Mechanism.Should().Be("project-key");
        fromJwt.Mechanism.Should().Be("jwt");
    }

    [Fact]
    public void ReadsBackFromTheHttpContext()
    {
        DefaultHttpContext context = new();
        RequestPrincipal principal = new("acme", DocumentScope.Read, RequestPrincipal.JwtMechanism);
        context.Items[RequestPrincipal.ItemKey] = principal;

        RequestPrincipal.Of(context).Should().BeSameAs(principal);
        RequestPrincipal.Require(context).Should().BeSameAs(principal);
    }

    [Fact]
    public void ReturnsNullWhenNoPrincipalIsAttached() =>
        RequestPrincipal.Of(new DefaultHttpContext()).Should().BeNull();

    [Fact]
    public void RequireThrowsRatherThanLettingAnUnscopedQueryRun()
    {
        Action act = () => RequestPrincipal.Require(new DefaultHttpContext());

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("tenant resolution middleware must run");
    }

    [Fact]
    public void HealthReadyAndMetricsAreTheOnlyAnonymousPaths() =>
        TenantResolutionMiddleware.AnonymousPaths
            .Should().BeEquivalentTo(["/health", "/ready", "/metrics"]);

    [Theory]
    [InlineData("/health", true)]
    [InlineData("/ready", true)]
    [InlineData("/metrics", true)]
    [InlineData("/HEALTH", true)]
    [InlineData("/api/v1/collections", false)]
    [InlineData("/health/providers", false)]
    [InlineData("/healthz", false)]
    [InlineData("/", false)]
    public void RecognisesAnonymousPathsExactly(string path, bool expected) =>
        TenantResolutionMiddleware.IsAnonymous(new PathString(path)).Should().Be(expected);
}
