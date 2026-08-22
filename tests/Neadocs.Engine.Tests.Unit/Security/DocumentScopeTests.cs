namespace Neadocs.Engine.Tests.Unit.Security;

using FluentAssertions;
using Neadocs.Engine.Infrastructure.Security;

public sealed class DocumentScopeTests
{
    [Theory]
    [InlineData("docs:read", DocumentScope.Read)]
    [InlineData("docs:write", DocumentScope.Write)]
    [InlineData("docs:admin", DocumentScope.Admin)]
    [InlineData("DOCS:ADMIN", DocumentScope.Admin)]
    [InlineData("Docs:Write", DocumentScope.Write)]
    public void ParsesAKnownScopeName(string value, DocumentScope expected) =>
        DocumentScopeNames.Parse(value).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("read")]
    [InlineData("docs:delete")]
    [InlineData("admin")]
    [InlineData("docs:read:extra")]
    public void ParsesAnUnknownScopeNameAsNone(string value) =>
        DocumentScopeNames.Parse(value).Should().Be(DocumentScope.None);

    [Fact]
    public void ParsesNullAsNone() =>
        DocumentScopeNames.Parse(null).Should().Be(DocumentScope.None);

    [Theory]
    [InlineData("docs:read docs:write", DocumentScope.Read | DocumentScope.Write)]
    [InlineData("docs:admin", DocumentScope.Admin)]
    [InlineData("docs:read,docs:admin", DocumentScope.Read | DocumentScope.Admin)]
    [InlineData("  docs:read   docs:write  ", DocumentScope.Read | DocumentScope.Write)]
    public void ParsesASpaceDelimitedScopeClaim(string value, DocumentScope expected) =>
        DocumentScopeNames.ParseDelimited(value).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nothing useful here")]
    public void ParsesAnEmptyOrUnrecognisedClaimAsNone(string value) =>
        DocumentScopeNames.ParseDelimited(value).Should().Be(DocumentScope.None);

    [Fact]
    public void ParsesManyClaimValues() =>
        DocumentScopeNames.ParseMany(["docs:read", "docs:write"])
            .Should().Be(DocumentScope.Read | DocumentScope.Write);

    [Fact]
    public void AdminGrantsWriteAndRead()
    {
        DocumentScope admin = DocumentScope.Admin;

        admin.Grants(DocumentScope.Admin).Should().BeTrue();
        admin.Grants(DocumentScope.Write).Should().BeTrue();
        admin.Grants(DocumentScope.Read).Should().BeTrue();
    }

    [Fact]
    public void WriteGrantsReadButNotAdmin()
    {
        DocumentScope write = DocumentScope.Write;

        write.Grants(DocumentScope.Write).Should().BeTrue();
        write.Grants(DocumentScope.Read).Should().BeTrue();
        write.Grants(DocumentScope.Admin).Should().BeFalse();
    }

    [Fact]
    public void ReadGrantsOnlyRead()
    {
        DocumentScope read = DocumentScope.Read;

        read.Grants(DocumentScope.Read).Should().BeTrue();
        read.Grants(DocumentScope.Write).Should().BeFalse();
        read.Grants(DocumentScope.Admin).Should().BeFalse();
    }

    [Fact]
    public void NoneGrantsNothing()
    {
        DocumentScope none = DocumentScope.None;

        none.Grants(DocumentScope.Read).Should().BeFalse();
        none.Grants(DocumentScope.Write).Should().BeFalse();
        none.Grants(DocumentScope.Admin).Should().BeFalse();
    }

    [Fact]
    public void EveryScopeSatisfiesAnEmptyRequirement()
    {
        DocumentScope.None.Grants(DocumentScope.None).Should().BeTrue();
        DocumentScope.Read.Grants(DocumentScope.None).Should().BeTrue();
    }

    [Fact]
    public void ExpandingIsIdempotent()
    {
        DocumentScope once = DocumentScope.Admin.Expand();

        once.Expand().Should().Be(once);
    }

    [Fact]
    public void ExpandingAdminYieldsAllThree() =>
        DocumentScope.Admin.Expand()
            .Should().Be(DocumentScope.Admin | DocumentScope.Write | DocumentScope.Read);

    [Fact]
    public void FormatsTheHeldScopesMostPowerfulFirst() =>
        DocumentScopeNames.Format(DocumentScope.Admin.Expand())
            .Should().Be("docs:admin docs:write docs:read");

    [Fact]
    public void FormatsNoneAsEmpty() =>
        DocumentScopeNames.Format(DocumentScope.None).Should().BeEmpty();

    [Fact]
    public void DeclaresExactlyThreeScopeNames() =>
        DocumentScopeNames.All.Should().BeEquivalentTo(["docs:read", "docs:write", "docs:admin"]);
}
