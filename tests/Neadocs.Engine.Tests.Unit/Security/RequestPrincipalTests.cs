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

    /// <summary>
    /// The exact set, so widening it is always a deliberate act.
    /// </summary>
    /// <remarks>
    /// Grew from three to five when the Kubernetes probe paths were added. The kubelet presents no
    /// API key, so a probe path that answers 401 fails the probe and a failing startup probe kills
    /// the pod — this service served liveness at /health and readiness at /ready while its
    /// manifest probed /health/live and /health/ready, and adding the endpoints alone was not
    /// enough because they still 401'd here.
    ///
    /// Anything else added to this list should have to argue for itself, which is what this test
    /// is for.
    /// </remarks>
    [Fact]
    public void OnlyTheProbeAndMetricsPathsAreAnonymous() =>
        TenantResolutionMiddleware.AnonymousPaths
            .Should().BeEquivalentTo(["/health", "/health/live", "/ready", "/health/ready", "/metrics"]);

    [Theory]
    [InlineData("/health", true)]
    [InlineData("/health/live", true)]
    [InlineData("/ready", true)]
    [InlineData("/health/ready", true)]
    [InlineData("/metrics", true)]
    [InlineData("/HEALTH", true)]
    [InlineData("/api/v1/collections", false)]
    [InlineData("/health/providers", false)]
    [InlineData("/healthz", false)]
    [InlineData("/", false)]
    public void RecognisesAnonymousPathsExactly(string path, bool expected) =>
        TenantResolutionMiddleware.IsAnonymous(new PathString(path)).Should().Be(expected);
}
