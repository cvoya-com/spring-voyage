// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the free-functions on <see cref="ExpertiseCommand"/> that
/// can be tested without spinning up the CLI pipeline (#412).
/// </summary>
public class ExpertiseCommandTests
{
    [Fact]
    public void ParseDomainSpec_NameOnly_LeavesLevelAndDescriptionEmpty()
    {
        var domain = ExpertiseCommand.ParseDomainSpec("python/fastapi");
        domain.Name.ShouldBe("python/fastapi");
        domain.Level.ShouldBeNull();
        domain.Description.ShouldBeEmpty();
    }

    [Fact]
    public void ParseDomainSpec_NameAndLevel_SetsLevel()
    {
        var domain = ExpertiseCommand.ParseDomainSpec("python/fastapi:expert");
        domain.Name.ShouldBe("python/fastapi");
        domain.Level.ShouldBe("expert");
        domain.Description.ShouldBeEmpty();
    }

    [Fact]
    public void ParseDomainSpec_NameLevelAndDescription_ParsesAllThree()
    {
        var domain = ExpertiseCommand.ParseDomainSpec("python/fastapi:advanced:Server-side async APIs");
        domain.Name.ShouldBe("python/fastapi");
        domain.Level.ShouldBe("advanced");
        domain.Description.ShouldBe("Server-side async APIs");
    }

    [Fact]
    public void ParseDomainSpec_DescriptionCanContainColons()
    {
        // Only the first two colons are delimiters — the remainder is the
        // description verbatim, so a domain spec can include URLs or ratios.
        var domain = ExpertiseCommand.ParseDomainSpec("http:intermediate:See https://example.com/docs");
        domain.Name.ShouldBe("http");
        domain.Level.ShouldBe("intermediate");
        domain.Description.ShouldBe("See https://example.com/docs");
    }

    [Fact]
    public void ParseDomainSpec_InvalidLevel_Throws()
    {
        Should.Throw<System.ArgumentException>(() => ExpertiseCommand.ParseDomainSpec("python:guru"));
    }

    [Fact]
    public void ParseDomainSpec_EmptySpec_Throws()
    {
        Should.Throw<System.ArgumentException>(() => ExpertiseCommand.ParseDomainSpec(""));
    }

    [Theory]
    [InlineData("python:BEGINNER", "beginner")]
    [InlineData("python:Advanced", "advanced")]
    public void ParseDomainSpec_LevelIsLowercasedForWireConsistency(string spec, string expected)
    {
        var domain = ExpertiseCommand.ParseDomainSpec(spec);
        domain.Level.ShouldBe(expected);
    }
}