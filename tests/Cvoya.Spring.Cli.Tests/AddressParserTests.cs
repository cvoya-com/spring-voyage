// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using FluentAssertions;
using Xunit;

public class AddressParserTests
{
    [Fact]
    public void Parse_ValidAddress_ReturnsSchemeAndPath()
    {
        var (scheme, path) = AddressParser.Parse("agent://ada");

        scheme.Should().Be("agent");
        path.Should().Be("ada");
    }

    [Fact]
    public void Parse_UnitAddress_ReturnsSchemeAndPath()
    {
        var (scheme, path) = AddressParser.Parse("unit://engineering");

        scheme.Should().Be("unit");
        path.Should().Be("engineering");
    }

    [Fact]
    public void Parse_InvalidFormat_ThrowsFormatException()
    {
        var act = () => AddressParser.Parse("invalid-address");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_EmptyScheme_ThrowsFormatException()
    {
        var act = () => AddressParser.Parse("://path");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_EmptyPath_ThrowsFormatException()
    {
        var act = () => AddressParser.Parse("agent://");

        act.Should().Throw<FormatException>();
    }
}
