// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.Validation;

using Cvoya.Spring.Core.Validation;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DisplayNameValidator"/> (#1632). The Guid-shape
/// check is the load-bearing rule: every standard format must be rejected so
/// the addressing surface defined by #1629 stays unambiguous.
/// </summary>
public class DisplayNameValidatorTests
{
    // A concrete Guid we keep reusing across the format-coverage tests so
    // each rejection is for the same conceptual id rendered in a different
    // shape. Lower-case to also exercise the case-insensitivity path.
    private const string SampleGuidNoDash = "8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7";
    private const string SampleGuidDashed = "8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7";
    private const string SampleGuidBraced = "{8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7}";
    private const string SampleGuidParens = "(8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7)";

    // Hex-block form. `Guid.TryParse` does NOT accept this; only
    // `Guid.TryParseExact(.., "X")` does — which is why the validator
    // fans out across all five formats explicitly.
    private const string SampleGuidHexBlock =
        "{0x8c5fab2a,0x8e7e,0x4b9c,{0x92,0xf1,0xd8,0xa3,0xb4,0xc5,0xd6,0xe7}}";

    [Fact]
    public void Validate_Null_ReturnsEmptyErrorCode()
    {
        DisplayNameValidator.Validate(null).ShouldBe(DisplayNameValidator.EmptyErrorCode);
    }

    [Fact]
    public void Validate_EmptyString_ReturnsEmptyErrorCode()
    {
        DisplayNameValidator.Validate(string.Empty).ShouldBe(DisplayNameValidator.EmptyErrorCode);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \t  \r\n")]
    public void Validate_WhitespaceOnly_ReturnsEmptyErrorCode(string input)
    {
        DisplayNameValidator.Validate(input).ShouldBe(DisplayNameValidator.EmptyErrorCode);
    }

    [Fact]
    public void Validate_GuidNoDashForm_ReturnsGuidShapeErrorCode()
    {
        DisplayNameValidator.Validate(SampleGuidNoDash)
            .ShouldBe(DisplayNameValidator.GuidShapeErrorCode);
    }

    [Fact]
    public void Validate_GuidDashedForm_ReturnsGuidShapeErrorCode()
    {
        DisplayNameValidator.Validate(SampleGuidDashed)
            .ShouldBe(DisplayNameValidator.GuidShapeErrorCode);
    }

    [Fact]
    public void Validate_GuidBracedForm_ReturnsGuidShapeErrorCode()
    {
        DisplayNameValidator.Validate(SampleGuidBraced)
            .ShouldBe(DisplayNameValidator.GuidShapeErrorCode);
    }

    [Fact]
    public void Validate_GuidParenthesisedForm_ReturnsGuidShapeErrorCode()
    {
        DisplayNameValidator.Validate(SampleGuidParens)
            .ShouldBe(DisplayNameValidator.GuidShapeErrorCode);
    }

    [Fact]
    public void Validate_GuidHexBlockForm_ReturnsGuidShapeErrorCode()
    {
        DisplayNameValidator.Validate(SampleGuidHexBlock)
            .ShouldBe(DisplayNameValidator.GuidShapeErrorCode);
    }

    [Fact]
    public void Validate_GuidWithSurroundingWhitespace_ReturnsGuidShapeErrorCode()
    {
        DisplayNameValidator.Validate($"   {SampleGuidDashed}   ")
            .ShouldBe(DisplayNameValidator.GuidShapeErrorCode);
    }

    [Fact]
    public void Validate_GuidUpperCase_ReturnsGuidShapeErrorCode()
    {
        DisplayNameValidator.Validate(SampleGuidDashed.ToUpperInvariant())
            .ShouldBe(DisplayNameValidator.GuidShapeErrorCode);
    }

    [Fact]
    public void Validate_GuidMixedCase_ReturnsGuidShapeErrorCode()
    {
        // Mix of upper and lower case hex letters; Guid parsing is
        // case-insensitive, so the validator must reject this just as
        // firmly as the all-lower-case form.
        DisplayNameValidator.Validate("8C5fAB2a-8E7e-4B9c-92F1-D8a3B4c5D6e7")
            .ShouldBe(DisplayNameValidator.GuidShapeErrorCode);
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("Engineering Team")]
    [InlineData("backend-platform")]
    [InlineData("Façade")]
    [InlineData("名前")]
    public void Validate_NormalNames_ReturnNull(string input)
    {
        DisplayNameValidator.Validate(input).ShouldBeNull();
    }

    [Fact]
    public void Validate_NameContainingGuidSubstring_ReturnsNull()
    {
        // Substring containment is fine — only the *whole* string parsing
        // as a Guid is the collision class we reject.
        var name = $"Alice {SampleGuidDashed} Smith";
        DisplayNameValidator.Validate(name).ShouldBeNull();
    }

    [Theory]
    [InlineData("hello\nworld")]
    [InlineData("hello\tworld")]
    [InlineData("hello\rworld")]
    [InlineData("hello\0world")]
    public void Validate_ControlCharacters_ReturnsControlCharsErrorCode(string input)
    {
        DisplayNameValidator.Validate(input)
            .ShouldBe(DisplayNameValidator.ControlCharsErrorCode);
    }

    [Fact]
    public void Validate_LongerThanMaxLength_ReturnsTooLongErrorCode()
    {
        var input = new string('A', DisplayNameValidator.MaxLength + 1);
        DisplayNameValidator.Validate(input).ShouldBe(DisplayNameValidator.TooLongErrorCode);
    }

    [Fact]
    public void Validate_ExactlyMaxLength_ReturnsNull()
    {
        var input = new string('A', DisplayNameValidator.MaxLength);
        DisplayNameValidator.Validate(input).ShouldBeNull();
    }

    [Fact]
    public void ThrowIfInvalid_ValidName_DoesNotThrow()
    {
        Should.NotThrow(() => DisplayNameValidator.ThrowIfInvalid("Alice"));
    }

    [Fact]
    public void ThrowIfInvalid_GuidShape_ThrowsArgumentExceptionCarryingErrorCode()
    {
        var ex = Should.Throw<ArgumentException>(() =>
            DisplayNameValidator.ThrowIfInvalid(SampleGuidDashed, "displayName"));

        ex.ParamName.ShouldBe("displayName");
        ex.Message.ShouldContain(DisplayNameValidator.GuidShapeErrorCode);
    }

    [Fact]
    public void ThrowIfInvalid_Empty_ThrowsArgumentExceptionCarryingErrorCode()
    {
        var ex = Should.Throw<ArgumentException>(() =>
            DisplayNameValidator.ThrowIfInvalid(string.Empty, "displayName"));

        ex.ParamName.ShouldBe("displayName");
        ex.Message.ShouldContain(DisplayNameValidator.EmptyErrorCode);
    }
}