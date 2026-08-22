namespace Neadocs.Engine.Tests.Unit.Security;

using System;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Security;

public sealed class ApiKeyValidatorTests
{
    [Fact]
    public void ResolvesTheTenantThatOwnsAKey()
    {
        ApiKeyValidator validator = new("acme:secret-a,globex:secret-b");

        validator.TryResolve("secret-b", out string tenant, out DocumentScope scopes)
            .Should().BeTrue();
        tenant.Should().Be("globex");
        scopes.Should().Be(DocumentScope.Admin);
    }

    [Fact]
    public void GrantsAdminWhenNoScopeSuffixIsGiven()
    {
        ApiKeyValidator validator = new("acme:secret");

        validator.TryResolve("secret", out _, out DocumentScope scopes).Should().BeTrue();
        scopes.Grants(DocumentScope.Admin).Should().BeTrue();
    }

    [Theory]
    [InlineData("read", DocumentScope.Read)]
    [InlineData("write", DocumentScope.Write)]
    [InlineData("admin", DocumentScope.Admin)]
    [InlineData("READ", DocumentScope.Read)]
    [InlineData("docs:read", DocumentScope.Read)]
    public void HonoursAScopeSuffix(string suffix, DocumentScope expected)
    {
        ApiKeyValidator validator = new($"acme:secret:{suffix}");

        validator.TryResolve("secret", out _, out DocumentScope scopes).Should().BeTrue();
        scopes.Should().Be(expected);
    }

    [Fact]
    public void CombinesSeveralScopesInASuffix()
    {
        ApiKeyValidator validator = new("acme:secret:read+write");

        validator.TryResolve("secret", out _, out DocumentScope scopes).Should().BeTrue();
        scopes.Should().Be(DocumentScope.Read | DocumentScope.Write);
        scopes.Grants(DocumentScope.Admin).Should().BeFalse();
    }

    [Fact]
    public void AReadOnlyKeyCannotWrite()
    {
        ApiKeyValidator validator = new("acme:secret:read");

        validator.TryResolve("secret", out _, out DocumentScope scopes).Should().BeTrue();
        scopes.Grants(DocumentScope.Write).Should().BeFalse();
    }

    [Fact]
    public void RejectsAnUnknownKey()
    {
        ApiKeyValidator validator = new("acme:secret");

        validator.TryResolve("wrong", out string tenant, out DocumentScope scopes).Should().BeFalse();
        tenant.Should().BeEmpty();
        scopes.Should().Be(DocumentScope.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RejectsAnAbsentKey(string? presented)
    {
        ApiKeyValidator validator = new("acme:secret");

        validator.TryResolve(presented, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void RejectsEverythingWhenNoKeysAreConfigured()
    {
        ApiKeyValidator validator = new("");

        validator.Count.Should().Be(0);
        validator.TryResolve("anything", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void MatchesTheKeyExactlyAndIsNotAPrefixMatch()
    {
        ApiKeyValidator validator = new("acme:secret");

        validator.TryResolve("secret-extra", out _, out _).Should().BeFalse();
        validator.TryResolve("secre", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void MatchesTheKeyCaseSensitively()
    {
        ApiKeyValidator validator = new("acme:Secret");

        validator.TryResolve("secret", out _, out _).Should().BeFalse();
        validator.TryResolve("Secret", out _, out _).Should().BeTrue();
    }

    [Fact]
    public void TolerantOfWhitespaceAroundEntries()
    {
        ApiKeyValidator validator = new("  acme : secret-a , globex : secret-b  ");

        validator.Count.Should().Be(2);
        validator.TryResolve("secret-a", out string tenant, out _).Should().BeTrue();
        tenant.Should().Be("acme");
    }

    [Fact]
    public void SkipsEmptyEntries()
    {
        ApiKeyValidator validator = new("acme:secret,,globex:other,");

        validator.Count.Should().Be(2);
    }

    [Fact]
    public void ReadsFromOptions()
    {
        DocumentEngineOptions options = new() { AllowedProjectKeys = "acme:secret" };

        ApiKeyValidator validator = new(options);

        validator.TryResolve("secret", out string tenant, out _).Should().BeTrue();
        tenant.Should().Be("acme");
    }

    [Theory]
    [InlineData("noseparator")]
    [InlineData(":leadingcolon")]
    [InlineData("trailingcolon:")]
    public void RefusesAMalformedEntry(string entry)
    {
        Action act = () => _ = new ApiKeyValidator(entry);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("AllowedProjectKeys").And.Contain(entry.Trim());
    }

    [Fact]
    public void RefusesAnUnknownScopeSuffix()
    {
        Action act = () => _ = new ApiKeyValidator("acme:secret:delete");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("unknown scope").And.Contain("delete");
    }

    [Fact]
    public void RefusesAnEmptyScopeSuffix()
    {
        Action act = () => _ = new ApiKeyValidator("acme:secret:");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("empty scope list");
    }

    [Fact]
    public void TwoTenantsMayNotBeReachedWithOneAnothersKey()
    {
        ApiKeyValidator validator = new("acme:key-a,globex:key-b");

        validator.TryResolve("key-a", out string first, out _).Should().BeTrue();
        validator.TryResolve("key-b", out string second, out _).Should().BeTrue();

        first.Should().Be("acme");
        second.Should().Be("globex");
        first.Should().NotBe(second);
    }

    [Fact]
    public void TheFirstConfiguredEntryWinsWhenTwoTenantsShareAKey()
    {
        ApiKeyValidator validator = new("acme:same,globex:same");

        validator.TryResolve("same", out string tenant, out _).Should().BeTrue();
        tenant.Should().Be("acme");
    }

    [Fact]
    public void TheHeaderNameIsTheDocumentedOne() =>
        ApiKeyValidator.HeaderName.Should().Be("X-Project-Key");
}
