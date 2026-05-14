// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using Shouldly;

using Xunit;

public class AddressParserTests
{
    private const string CanonicalNoDash = "8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7";
    private const string CanonicalDashed = "8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7";

    [Fact]
    public void Parse_CanonicalAgentAddress_ReturnsSchemeAndNoDashPath()
    {
        var (scheme, path) = AddressParser.Parse($"agent:{CanonicalNoDash}");

        scheme.ShouldBe("agent");
        path.ShouldBe(CanonicalNoDash);
    }

    [Fact]
    public void Parse_CanonicalUnitAddress_ReturnsSchemeAndNoDashPath()
    {
        var (scheme, path) = AddressParser.Parse($"unit:{CanonicalNoDash}");

        scheme.ShouldBe("unit");
        path.ShouldBe(CanonicalNoDash);
    }

    [Fact]
    public void Parse_DashedGuid_NormalisesToNoDashPath()
    {
        // Lenient parsing on input — emit the canonical no-dash form.
        var (scheme, path) = AddressParser.Parse($"agent:{CanonicalDashed}");

        scheme.ShouldBe("agent");
        path.ShouldBe(CanonicalNoDash);
    }

    [Fact]
    public void Parse_LegacySchemePathShape_ThrowsFormatException()
    {
        // Pre-ADR-0036 form is gone — `agent://ada` no longer parses.
        Should.Throw<FormatException>(() => AddressParser.Parse("agent://ada"));
    }

    [Fact]
    public void Parse_LegacySchemePathShapeWithGuid_ThrowsFormatException()
    {
        // Even with a Guid path the `://` shape is rejected — there is one
        // canonical wire form (`scheme:<guid>`).
        Should.Throw<FormatException>(() => AddressParser.Parse($"agent://{CanonicalNoDash}"));
    }

    [Fact]
    public void Parse_BareGuid_ThrowsFormatException()
    {
        // Bare Guids are accepted by `CliResolver` for `show` commands —
        // address parsing requires a scheme.
        Should.Throw<FormatException>(() => AddressParser.Parse(CanonicalNoDash));
    }

    [Fact]
    public void Parse_EmptyScheme_ThrowsFormatException()
    {
        Should.Throw<FormatException>(() => AddressParser.Parse($":{CanonicalNoDash}"));
    }

    [Fact]
    public void Parse_EmptyPath_ThrowsFormatException()
    {
        Should.Throw<FormatException>(() => AddressParser.Parse("agent:"));
    }

    [Fact]
    public void Parse_NonGuidPath_ThrowsFormatException()
    {
        // Path must parse as a Guid; non-Guid strings (slugs, names) are not
        // accepted at this surface — see CliResolver for name → Guid.
        Should.Throw<FormatException>(() => AddressParser.Parse("agent:ada"));
    }

    [Fact]
    public void Parse_EmptyString_ThrowsFormatException()
    {
        Should.Throw<FormatException>(() => AddressParser.Parse(string.Empty));
    }
}
