// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Units;

using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Units;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitPermissionCoordinator"/> exercised directly
/// (without going through <c>UnitActor</c>) to validate inheritance-flag
/// management in isolation.
/// </summary>
/// <remarks>
/// Pre-#2044 this suite also exercised the per-unit (humanId → entry) map.
/// After #2044 / ADR-0040 the grant surface lives on
/// <c>IUnitHumanPermissionRepository</c> + <c>UnitHumanPermissionStore</c>;
/// coverage for those pieces lives in
/// <c>UnitHumanPermissionRepositoryTests</c> and
/// <c>PermissionServiceTests</c>. This file shrinks to the inheritance-flag
/// tests that remain on the coordinator.
/// </remarks>
public class UnitPermissionCoordinatorTests
{
    private const string UnitActorId = "test-unit";

    private readonly ILogger<UnitPermissionCoordinator> _logger =
        Substitute.For<ILogger<UnitPermissionCoordinator>>();

    private readonly UnitPermissionCoordinator _coordinator;

    public UnitPermissionCoordinatorTests()
    {
        _coordinator = new UnitPermissionCoordinator(logger: _logger);
    }

    // --- GetPermissionInheritanceAsync ---

    [Fact]
    public async Task GetPermissionInheritanceAsync_AbsentState_ReturnsInherit()
    {
        // ADR-0013: absent state key means Inherit — ancestor grants cascade by default.
        var result = await _coordinator.GetPermissionInheritanceAsync(
            unitActorId: UnitActorId,
            getInheritance: _ => Task.FromResult<UnitPermissionInheritance?>(null),
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(UnitPermissionInheritance.Inherit);
    }

    [Fact]
    public async Task GetPermissionInheritanceAsync_PersistedIsolated_ReturnsIsolated()
    {
        var result = await _coordinator.GetPermissionInheritanceAsync(
            unitActorId: UnitActorId,
            getInheritance: _ => Task.FromResult<UnitPermissionInheritance?>(UnitPermissionInheritance.Isolated),
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(UnitPermissionInheritance.Isolated);
    }

    // --- SetPermissionInheritanceAsync ---

    [Fact]
    public async Task SetPermissionInheritanceAsync_Isolated_CallsPersistWithValue()
    {
        UnitPermissionInheritance? persisted = null;
        var removeCalled = false;

        await _coordinator.SetPermissionInheritanceAsync(
            unitActorId: UnitActorId,
            inheritance: UnitPermissionInheritance.Isolated,
            persistInheritance: (v, _) => { persisted = v; return Task.CompletedTask; },
            removeInheritance: _ => { removeCalled = true; return Task.CompletedTask; },
            cancellationToken: TestContext.Current.CancellationToken);

        persisted.ShouldBe(UnitPermissionInheritance.Isolated);
        removeCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task SetPermissionInheritanceAsync_Inherit_CallsRemoveNotPersist()
    {
        // Writing the default clears state (row-deletion pattern) rather than
        // storing a no-op entry — consistent with the boundary actor and
        // ADR-0013's fail-closed posture.
        var persistCalled = false;
        var removeCalled = false;

        await _coordinator.SetPermissionInheritanceAsync(
            unitActorId: UnitActorId,
            inheritance: UnitPermissionInheritance.Inherit,
            persistInheritance: (_, _) => { persistCalled = true; return Task.CompletedTask; },
            removeInheritance: _ => { removeCalled = true; return Task.CompletedTask; },
            cancellationToken: TestContext.Current.CancellationToken);

        persistCalled.ShouldBeFalse();
        removeCalled.ShouldBeTrue();
    }
}
