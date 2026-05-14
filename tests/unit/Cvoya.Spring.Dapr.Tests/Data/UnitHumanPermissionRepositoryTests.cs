// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitHumanPermissionRepository"/> (#2044 / ADR-0040).
/// Verifies the upsert / delete / read paths against the EF in-memory
/// provider. The integration suite covers the full Postgres path; these
/// fast tests pin the (tenant, unit, human) uniqueness invariant and the
/// idempotent-delete contract that the actor and PermissionService rely on.
/// </summary>
public class UnitHumanPermissionRepositoryTests : IDisposable
{
    private static readonly Guid Unit1 = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Unit2 = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Human1 = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Human2 = new("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly SpringDbContext _context;
    private readonly UnitHumanPermissionRepository _repository;

    public UnitHumanPermissionRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new SpringDbContext(options);
        _repository = new UnitHumanPermissionRepository(_context);
    }

    [Fact]
    public async Task UpsertAsync_NewGrant_PersistsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var entry = new UnitPermissionEntry(Human1.ToString(), PermissionLevel.Owner, "Alice", true);

        await _repository.UpsertAsync(Unit1, Human1, entry, ct);

        var fetched = await _repository.GetAsync(Unit1, Human1, ct);
        fetched.ShouldNotBeNull();
        fetched!.Permission.ShouldBe(PermissionLevel.Owner);
        fetched.Identity.ShouldBe("Alice");
        fetched.Notifications.ShouldBeTrue();
    }

    [Fact]
    public async Task UpsertAsync_ExistingGrant_ReplacesPermission()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(Unit1, Human1,
            new UnitPermissionEntry(Human1.ToString(), PermissionLevel.Viewer), ct);
        await _repository.UpsertAsync(Unit1, Human1,
            new UnitPermissionEntry(Human1.ToString(), PermissionLevel.Owner, "Alice", false), ct);

        var fetched = await _repository.GetAsync(Unit1, Human1, ct);
        fetched.ShouldNotBeNull();
        fetched!.Permission.ShouldBe(PermissionLevel.Owner);
        fetched.Identity.ShouldBe("Alice");
        fetched.Notifications.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ExistingGrant_RemovesAndReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(Unit1, Human1,
            new UnitPermissionEntry(Human1.ToString(), PermissionLevel.Owner), ct);

        var deleted = await _repository.DeleteAsync(Unit1, Human1, ct);

        deleted.ShouldBeTrue();
        (await _repository.GetAsync(Unit1, Human1, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_AbsentGrant_ReturnsFalse()
    {
        // Idempotent contract: a delete that found nothing reports false so
        // the API endpoint can still answer 204 without branching.
        var ct = TestContext.Current.CancellationToken;

        var deleted = await _repository.DeleteAsync(Unit1, Human1, ct);

        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_AbsentGrant_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        (await _repository.GetAsync(Unit1, Human1, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ListByUnitAsync_ReturnsRowsScopedToUnit()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(Unit1, Human1,
            new UnitPermissionEntry(Human1.ToString(), PermissionLevel.Owner), ct);
        await _repository.UpsertAsync(Unit1, Human2,
            new UnitPermissionEntry(Human2.ToString(), PermissionLevel.Viewer), ct);
        await _repository.UpsertAsync(Unit2, Human1,
            new UnitPermissionEntry(Human1.ToString(), PermissionLevel.Operator), ct);

        var unit1Rows = await _repository.ListByUnitAsync(Unit1, ct);

        unit1Rows.Count.ShouldBe(2);
        unit1Rows.ShouldContain(e => e.Permission == PermissionLevel.Owner);
        unit1Rows.ShouldContain(e => e.Permission == PermissionLevel.Viewer);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
