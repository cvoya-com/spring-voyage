// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Units;

using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Units;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitPermissionCoordinator"/> exercised directly
/// (without going through <c>UnitActor</c>) to validate grant, revocation,
/// query, and inheritance-flag management in isolation.
/// </summary>
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

    // --- SetHumanPermissionAsync ---

    [Fact]
    public async Task SetHumanPermissionAsync_NewEntry_AddsToDictionaryAndPersists()
    {
        var permissions = new Dictionary<string, UnitPermissionEntry>();
        Dictionary<string, UnitPermissionEntry>? persisted = null;
        var entry = new UnitPermissionEntry("human-1", PermissionLevel.Operator, "Alice", true);

        await _coordinator.SetHumanPermissionAsync(
            unitActorId: UnitActorId,
            humanId: "human-1",
            entry: entry,
            getPermissions: _ => Task.FromResult(permissions),
            persistPermissions: (d, _) => { persisted = d; return Task.CompletedTask; },
            cancellationToken: TestContext.Current.CancellationToken);

        persisted.ShouldNotBeNull();
        persisted!.ContainsKey("human-1").ShouldBeTrue();
        persisted["human-1"].Permission.ShouldBe(PermissionLevel.Operator);
    }

    [Fact]
    public async Task SetHumanPermissionAsync_ExistingEntry_ReplacesAndPersists()
    {
        var permissions = new Dictionary<string, UnitPermissionEntry>
        {
            ["human-1"] = new("human-1", PermissionLevel.Viewer, "Alice", true)
        };
        var newEntry = new UnitPermissionEntry("human-1", PermissionLevel.Owner, "Alice", true);
        Dictionary<string, UnitPermissionEntry>? persisted = null;

        await _coordinator.SetHumanPermissionAsync(
            unitActorId: UnitActorId,
            humanId: "human-1",
            entry: newEntry,
            getPermissions: _ => Task.FromResult(permissions),
            persistPermissions: (d, _) => { persisted = d; return Task.CompletedTask; },
            cancellationToken: TestContext.Current.CancellationToken);

        persisted.ShouldNotBeNull();
        persisted!["human-1"].Permission.ShouldBe(PermissionLevel.Owner);
    }

    // --- GetHumanPermissionAsync ---

    [Fact]
    public async Task GetHumanPermissionAsync_ExistingHuman_ReturnsPermissionLevel()
    {
        var permissions = new Dictionary<string, UnitPermissionEntry>
        {
            ["human-1"] = new("human-1", PermissionLevel.Owner, "Alice", true)
        };

        var result = await _coordinator.GetHumanPermissionAsync(
            unitActorId: UnitActorId,
            humanId: "human-1",
            getPermissions: _ => Task.FromResult(permissions),
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(PermissionLevel.Owner);
    }

    [Fact]
    public async Task GetHumanPermissionAsync_UnknownHuman_ReturnsNull()
    {
        var permissions = new Dictionary<string, UnitPermissionEntry>();

        var result = await _coordinator.GetHumanPermissionAsync(
            unitActorId: UnitActorId,
            humanId: "unknown",
            getPermissions: _ => Task.FromResult(permissions),
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    // --- RemoveHumanPermissionAsync ---

    [Fact]
    public async Task RemoveHumanPermissionAsync_ExistingEntry_RemovesAndReturnsTrueAndPersists()
    {
        var permissions = new Dictionary<string, UnitPermissionEntry>
        {
            ["human-1"] = new("human-1", PermissionLevel.Owner, "Alice", true),
            ["human-2"] = new("human-2", PermissionLevel.Viewer, "Bob", false)
        };
        Dictionary<string, UnitPermissionEntry>? persisted = null;

        var result = await _coordinator.RemoveHumanPermissionAsync(
            unitActorId: UnitActorId,
            humanId: "human-1",
            getPermissions: _ => Task.FromResult(permissions),
            persistPermissions: (d, _) => { persisted = d; return Task.CompletedTask; },
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
        persisted.ShouldNotBeNull();
        persisted!.ContainsKey("human-1").ShouldBeFalse();
        persisted.ContainsKey("human-2").ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveHumanPermissionAsync_UnknownEntry_ReturnsFalseAndDoesNotPersist()
    {
        var permissions = new Dictionary<string, UnitPermissionEntry>();
        var persistCalled = false;

        var result = await _coordinator.RemoveHumanPermissionAsync(
            unitActorId: UnitActorId,
            humanId: "unknown",
            getPermissions: _ => Task.FromResult(permissions),
            persistPermissions: (_, _) => { persistCalled = true; return Task.CompletedTask; },
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeFalse();
        persistCalled.ShouldBeFalse();
    }

    // --- GetHumanPermissionsAsync ---

    [Fact]
    public async Task GetHumanPermissionsAsync_MultipleEntries_ReturnsAllValues()
    {
        var permissions = new Dictionary<string, UnitPermissionEntry>
        {
            ["human-1"] = new("human-1", PermissionLevel.Owner, "Alice", true),
            ["human-2"] = new("human-2", PermissionLevel.Viewer, "Bob", false)
        };

        var result = await _coordinator.GetHumanPermissionsAsync(
            unitActorId: UnitActorId,
            getPermissions: _ => Task.FromResult(permissions),
            cancellationToken: TestContext.Current.CancellationToken);

        result.Length.ShouldBe(2);
        result.ShouldContain(e => e.HumanId == "human-1" && e.Permission == PermissionLevel.Owner);
        result.ShouldContain(e => e.HumanId == "human-2" && e.Permission == PermissionLevel.Viewer);
    }

    [Fact]
    public async Task GetHumanPermissionsAsync_EmptyDictionary_ReturnsEmptyArray()
    {
        var permissions = new Dictionary<string, UnitPermissionEntry>();

        var result = await _coordinator.GetHumanPermissionsAsync(
            unitActorId: UnitActorId,
            getPermissions: _ => Task.FromResult(permissions),
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
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