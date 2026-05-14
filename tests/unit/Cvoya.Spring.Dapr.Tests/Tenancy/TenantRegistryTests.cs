// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tenancy;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="TenantRegistry"/> — the EF-backed
/// implementation of <see cref="ITenantRegistry"/>. Exercises the
/// happy paths, the duplicate guard, and the soft-delete contract.
///
/// Post #1629: tenant ids are <see cref="Guid"/>; the slug-shape
/// validation that used to gate string ids has been removed.
/// </summary>
public class TenantRegistryTests
{
    private static readonly Guid AcmeId = new("00000001-0000-0000-0000-000000000001");
    private static readonly Guid DefaultId = new("dddddddd-0000-0000-0000-000000000000");

    [Fact]
    public async Task CreateAsync_HappyPath_PersistsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var record = await sut.CreateAsync(AcmeId, "ACME Corp", ct);

        record.Id.ShouldBe(AcmeId);
        record.DisplayName.ShouldBe("ACME Corp");
        record.State.ShouldBe(TenantState.Active);
        record.CreatedAt.ShouldBe(record.UpdatedAt);

        var fetched = await sut.GetAsync(AcmeId, ct);
        fetched.ShouldNotBeNull();
        fetched.Id.ShouldBe(AcmeId);
    }

    [Fact]
    public async Task CreateAsync_NullDisplayName_FallsBackToId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var record = await sut.CreateAsync(AcmeId, displayName: null, ct);

        // Falls back to the Guid wire form when display name is null.
        record.DisplayName.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateAsync_WhitespaceDisplayName_FallsBackToId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var record = await sut.CreateAsync(AcmeId, displayName: "  ", ct);

        record.DisplayName.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateAsync_DuplicateId_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var dupId = new Guid("00000002-0000-0000-0000-000000000002");
        await sut.CreateAsync(dupId, null, ct);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.CreateAsync(dupId, null, ct));
    }

    [Fact]
    public async Task CreateAsync_DuplicateAfterSoftDelete_ThrowsInvalidOperationException()
    {
        // v0.1 keeps restoration deliberately out of scope — the row
        // remains in place after a soft-delete and CreateAsync surfaces
        // a clear error rather than silently re-creating.
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var recyclableId = new Guid("00000003-0000-0000-0000-000000000003");
        await sut.CreateAsync(recyclableId, null, ct);
        var deleted = await sut.DeleteAsync(recyclableId, ct);
        deleted.ShouldBeTrue();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.CreateAsync(recyclableId, null, ct));
        ex.Message.ShouldContain("soft-deleted");
    }

    [Fact]
    public async Task ListAsync_OrdersById()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var alpha = new Guid("00000010-0000-0000-0000-00000000000a");
        var middle = new Guid("00000020-0000-0000-0000-00000000000b");
        var zeta = new Guid("00000030-0000-0000-0000-00000000000c");

        await sut.CreateAsync(zeta, null, ct);
        await sut.CreateAsync(alpha, null, ct);
        await sut.CreateAsync(middle, null, ct);

        var list = await sut.ListAsync(ct);
        list.Select(t => t.Id).ShouldBe(new[] { alpha, middle, zeta });
    }

    [Fact]
    public async Task ListAsync_ExcludesSoftDeletedRows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var kept = new Guid("00000040-0000-0000-0000-00000000000d");
        var removed = new Guid("00000050-0000-0000-0000-00000000000e");
        await sut.CreateAsync(kept, null, ct);
        await sut.CreateAsync(removed, null, ct);
        await sut.DeleteAsync(removed, ct);

        var list = await sut.ListAsync(ct);
        list.Select(t => t.Id).ShouldBe(new[] { kept });
    }

    [Fact]
    public async Task GetAsync_SoftDeletedRow_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var ghost = new Guid("00000060-0000-0000-0000-00000000000f");
        await sut.CreateAsync(ghost, null, ct);
        await sut.DeleteAsync(ghost, ct);

        var fetched = await sut.GetAsync(ghost, ct);
        fetched.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateAsync_HappyPath_RefreshesUpdatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var updId = new Guid("00000070-0000-0000-0000-000000000010");
        var created = await sut.CreateAsync(updId, "Original", ct);

        // EF in-memory persists wall-clock; pause briefly so UpdatedAt advances.
        await Task.Delay(5, ct);
        var updated = await sut.UpdateAsync(updId, "Updated", ct);

        updated.ShouldNotBeNull();
        updated.DisplayName.ShouldBe("Updated");
        updated.UpdatedAt.ShouldBeGreaterThan(created.UpdatedAt);
    }

    [Fact]
    public async Task UpdateAsync_NullDisplayName_LeavesValueUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var noopId = new Guid("00000080-0000-0000-0000-000000000011");
        await sut.CreateAsync(noopId, "Original", ct);
        var updated = await sut.UpdateAsync(noopId, displayName: null, ct);

        updated.ShouldNotBeNull();
        updated.DisplayName.ShouldBe("Original");
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var updated = await sut.UpdateAsync(Guid.NewGuid(), "X", ct);
        updated.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_HappyPath_ReturnsTrueAndSoftDeletes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var killId = new Guid("00000090-0000-0000-0000-000000000012");
        await sut.CreateAsync(killId, null, ct);
        var deleted = await sut.DeleteAsync(killId, ct);

        deleted.ShouldBeTrue();
        (await sut.GetAsync(killId, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var deleted = await sut.DeleteAsync(Guid.NewGuid(), ct);
        deleted.ShouldBeFalse();
    }

    private static SpringDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: $"TenantRegistryTests-{Guid.NewGuid():N}")
            .Options;
        return new SpringDbContext(options, new StaticTenantContext(DefaultId));
    }

    private static TenantRegistry CreateSut(SpringDbContext context)
    {
        return new TenantRegistry(
            context,
            new TenantScopeBypass(NullLogger<TenantScopeBypass>.Instance),
            NullLogger<TenantRegistry>.Instance);
    }
}
