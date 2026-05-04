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
/// Post-#1629 PR5 the parent-unit ids are typed Guids on the wire; the
/// validator dedupes/strips Guid.Empty rather than blank strings.
/// </summary>
public class UnitCreationServiceParentValidationTests
{
    private static readonly Guid EngTeam = Guid.Parse("a1a1a1a1-1111-1111-1111-aaaaaaaaaaaa");
    private static readonly Guid Ops = Guid.Parse("b2b2b2b2-2222-2222-2222-bbbbbbbbbbbb");

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
                parentUnitIds: Array.Empty<Guid>(),
                isTopLevel: false));
    }

    [Fact]
    public void ValidateParentRequest_OnlyEmptyGuidEntries_Throws()
    {
        // Normalisation strips Guid.Empty entries; once stripped this is the
        // "neither" case, not the "parent=Guid.Empty" case.
        Should.Throw<InvalidUnitParentRequestException>(() =>
            UnitCreationService.ValidateParentRequest(
                parentUnitIds: new[] { Guid.Empty, Guid.Empty },
                isTopLevel: null));
    }

    [Fact]
    public void ValidateParentRequest_BothSupplied_Throws()
    {
        Should.Throw<InvalidUnitParentRequestException>(() =>
            UnitCreationService.ValidateParentRequest(
                parentUnitIds: new[] { EngTeam },
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
            parentUnitIds: new[] { EngTeam, Guid.Empty, EngTeam, Ops },
            isTopLevel: null);

        result.IsTopLevel.ShouldBeFalse();
        result.ParentUnitIds.ShouldBe(new[] { EngTeam, Ops });
    }
}