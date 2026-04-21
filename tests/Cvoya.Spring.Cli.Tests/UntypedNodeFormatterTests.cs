// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System.Collections.Generic;

using Cvoya.Spring.Cli.Utilities;

using Microsoft.Kiota.Abstractions.Serialization;

using Shouldly;

using Xunit;

/// <summary>
/// Pins <see cref="UntypedNodeFormatter.FormatScalar"/>'s contract.
/// Closes #986: the CLI's row builders passed Kiota's <c>UntypedNode</c>
/// wrappers straight through <c>ToString()</c>, which returned the full
/// type name instead of the underlying scalar. These tests lock the
/// unwrap shape for each <c>Untyped*</c> subclass plus the null and
/// non-scalar fallbacks.
/// </summary>
public class UntypedNodeFormatterTests
{
    [Fact]
    public void FormatScalar_Null_ReturnsEmptyString()
    {
        UntypedNodeFormatter.FormatScalar(null).ShouldBe(string.Empty);
    }

    [Fact]
    public void FormatScalar_UntypedInteger_ReturnsNumericString()
    {
        UntypedNodeFormatter.FormatScalar(new UntypedInteger(3)).ShouldBe("3");
    }

    [Fact]
    public void FormatScalar_UntypedLong_ReturnsNumericString()
    {
        UntypedNodeFormatter.FormatScalar(new UntypedLong(9_000_000_000L)).ShouldBe("9000000000");
    }

    [Fact]
    public void FormatScalar_UntypedString_ReturnsUnderlyingValue()
    {
        UntypedNodeFormatter.FormatScalar(new UntypedString("hello")).ShouldBe("hello");
    }

    [Fact]
    public void FormatScalar_UntypedStringWithNullValue_ReturnsEmptyString()
    {
        UntypedNodeFormatter.FormatScalar(new UntypedString(null)).ShouldBe(string.Empty);
    }

    [Fact]
    public void FormatScalar_UntypedBooleanTrue_ReturnsLowercaseTrue()
    {
        UntypedNodeFormatter.FormatScalar(new UntypedBoolean(true)).ShouldBe("true");
    }

    [Fact]
    public void FormatScalar_UntypedBooleanFalse_ReturnsLowercaseFalse()
    {
        UntypedNodeFormatter.FormatScalar(new UntypedBoolean(false)).ShouldBe("false");
    }

    [Fact]
    public void FormatScalar_UntypedDouble_UsesInvariantCulture()
    {
        // Invariant-culture rendering keeps the decimal separator as '.'
        // regardless of the operator's locale — important for CLI output
        // that scripts downstream are likely to parse.
        UntypedNodeFormatter.FormatScalar(new UntypedDouble(1.5d)).ShouldBe("1.5");
    }

    [Fact]
    public void FormatScalar_UntypedFloat_UsesInvariantCulture()
    {
        UntypedNodeFormatter.FormatScalar(new UntypedFloat(2.25f)).ShouldBe("2.25");
    }

    [Fact]
    public void FormatScalar_UntypedDecimal_UsesInvariantCulture()
    {
        UntypedNodeFormatter.FormatScalar(new UntypedDecimal(3.14m)).ShouldBe("3.14");
    }

    [Fact]
    public void FormatScalar_UntypedNull_ReturnsEmptyString()
    {
        UntypedNodeFormatter.FormatScalar(new UntypedNull()).ShouldBe(string.Empty);
    }

    [Fact]
    public void FormatScalar_UnknownSubtype_FallsBackToToString()
    {
        // UntypedObject is not a scalar. The formatter's job is to avoid
        // the "UntypedInteger class name" bug for scalars; for composite
        // shapes we defer to the default ToString() and let the caller
        // decide what to do (typically: render as empty / skip).
        var nested = new UntypedObject(new Dictionary<string, UntypedNode>
        {
            ["inner"] = new UntypedString("value"),
        });

        var rendered = UntypedNodeFormatter.FormatScalar(nested);

        rendered.ShouldNotBeNull();
    }
}