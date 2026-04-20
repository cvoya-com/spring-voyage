// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using Cvoya.Spring.Host.Api.Services;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitCreationService.ValidateParentRequest"/> — the
/// pure validation helper that classifies the "every unit has a parent"
/// inputs into <see cref="UnitParentInfo"/> or throws
/// <see cref="InvalidUnitParentRequestException"/> when neither / both
/// forms are supplied. Introduced with the review feedback on #744.
/// </summary>
public class UnitCreationServiceParentValidationTests
{
    [Fact]
    public void ValidateParentRequest_NeitherSupplied_Throws()
    {
        Should.Throw<InvalidUnitParentRequestException>(() =>
            UnitCreationService.ValidateParentRequest(
                parentUnitIds: null,
                isTopLevel: null));
    }

    [Fact]
    public void ValidateParentRequest_EmptyParentsNoTopLevel_Throws()
    {
        Should.Throw<InvalidUnitParentRequestException>(() =>
            UnitCreationService.ValidateParentRequest(
                parentUnitIds: Array.Empty<string>(),
                isTopLevel: false));
    }

    [Fact]
    public void ValidateParentRequest_WhitespaceOnlyParents_Throws()
    {
        // Normalisation strips blank entries; once stripped this is the
        // "neither" case, not the "parent=blank-value" case.
        Should.Throw<InvalidUnitParentRequestException>(() =>
            UnitCreationService.ValidateParentRequest(
                parentUnitIds: new[] { "  ", string.Empty },
                isTopLevel: null));
    }

    [Fact]
    public void ValidateParentRequest_BothSupplied_Throws()
    {
        Should.Throw<InvalidUnitParentRequestException>(() =>
            UnitCreationService.ValidateParentRequest(
                parentUnitIds: new[] { "eng-team" },
                isTopLevel: true));
    }

    [Fact]
    public void ValidateParentRequest_TopLevelOnly_Succeeds()
    {
        var result = UnitCreationService.ValidateParentRequest(
            parentUnitIds: null,
            isTopLevel: true);

        result.IsTopLevel.ShouldBeTrue();
        result.ParentUnitIds.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateParentRequest_ParentsOnly_Succeeds_NormalisesInput()
    {
        var result = UnitCreationService.ValidateParentRequest(
            parentUnitIds: new[] { "eng-team", "  ", "eng-team", "ops" },
            isTopLevel: null);

        result.IsTopLevel.ShouldBeFalse();
        result.ParentUnitIds.ShouldBe(new[] { "eng-team", "ops" });
    }
}