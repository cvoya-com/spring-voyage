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
/// happy paths, the slug-shape validation, the duplicate guard, and
/// the soft-delete contract.
/// </summary>
public class TenantRegistryTests
{
    [Fact]
    public async Task CreateAsync_HappyPath_PersistsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var record = await sut.CreateAsync("acme", "ACME Corp", ct);

        record.Id.ShouldBe("acme");
        record.DisplayName.ShouldBe("ACME Corp");
        record.State.ShouldBe(TenantState.Active);
        record.CreatedAt.ShouldBe(record.UpdatedAt);

        var fetched = await sut.GetAsync("acme", ct);
        fetched.ShouldNotBeNull();
        fetched.Id.ShouldBe("acme");
    }

    [Fact]
    public async Task CreateAsync_NullDisplayName_FallsBackToId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var record = await sut.CreateAsync("acme", displayName: null, ct);

        record.DisplayName.ShouldBe("acme");
    }

    [Fact]
    public async Task CreateAsync_WhitespaceDisplayName_FallsBackToId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var record = await sut.CreateAsync("acme", displayName: "  ", ct);

        record.DisplayName.ShouldBe("acme");
    }

    [Theory]
    [InlineData("Acme")]                  // upper-case
    [InlineData("acme!")]                 // disallowed punctuation
    [InlineData("-acme")]                 // leading hyphen
    [InlineData("a c m e")]               // spaces
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 chars (one over the max)
    public async Task CreateAsync_MalformedId_ThrowsArgumentException(string id)
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        await Should.ThrowAsync<ArgumentException>(() =>
            sut.CreateAsync(id, displayName: null, ct));
    }

    [Fact]
    public async Task CreateAsync_ShortestValidId_Succeeds()
    {
        // Positive control bracketing the 65-char invalid case in
        // CreateAsync_MalformedId_ThrowsArgumentException — single-letter
        // ids are the shortest accepted form.
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var record = await sut.CreateAsync("a", displayName: null, ct);
        record.Id.ShouldBe("a");
    }

    [Fact]
    public async Task CreateAsync_DuplicateId_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        await sut.CreateAsync("dup", null, ct);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.CreateAsync("dup", null, ct));
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

        await sut.CreateAsync("recyclable", null, ct);
        var deleted = await sut.DeleteAsync("recyclable", ct);
        deleted.ShouldBeTrue();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.CreateAsync("recyclable", null, ct));
        ex.Message.ShouldContain("soft-deleted");
    }

    [Fact]
    public async Task ListAsync_OrdersById()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        await sut.CreateAsync("zeta", null, ct);
        await sut.CreateAsync("alpha", null, ct);
        await sut.CreateAsync("middle", null, ct);

        var list = await sut.ListAsync(ct);
        list.Select(t => t.Id).ShouldBe(new[] { "alpha", "middle", "zeta" });
    }

    [Fact]
    public async Task ListAsync_ExcludesSoftDeletedRows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        await sut.CreateAsync("kept", null, ct);
        await sut.CreateAsync("removed", null, ct);
        await sut.DeleteAsync("removed", ct);

        var list = await sut.ListAsync(ct);
        list.Select(t => t.Id).ShouldBe(new[] { "kept" });
    }

    [Fact]
    public async Task GetAsync_SoftDeletedRow_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        await sut.CreateAsync("ghost", null, ct);
        await sut.DeleteAsync("ghost", ct);

        var fetched = await sut.GetAsync("ghost", ct);
        fetched.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateAsync_HappyPath_RefreshesUpdatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var created = await sut.CreateAsync("upd", "Original", ct);

        // EF in-memory persists wall-clock; pause briefly so UpdatedAt
        // advances.
        await Task.Delay(5, ct);
        var updated = await sut.UpdateAsync("upd", "Updated", ct);

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

        await sut.CreateAsync("noop", "Original", ct);
        var updated = await sut.UpdateAsync("noop", displayName: null, ct);

        updated.ShouldNotBeNull();
        updated.DisplayName.ShouldBe("Original");
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var updated = await sut.UpdateAsync("missing", "X", ct);
        updated.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_HappyPath_ReturnsTrueAndSoftDeletes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        await sut.CreateAsync("kill", null, ct);
        var deleted = await sut.DeleteAsync("kill", ct);

        deleted.ShouldBeTrue();
        (await sut.GetAsync("kill", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = CreateContext();
        var sut = CreateSut(context);

        var deleted = await sut.DeleteAsync("missing", ct);
        deleted.ShouldBeFalse();
    }

    private static SpringDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: $"TenantRegistryTests-{Guid.NewGuid():N}")
            .Options;
        return new SpringDbContext(options, new StaticTenantContext("default"));
    }

    private static TenantRegistry CreateSut(SpringDbContext context)
    {
        return new TenantRegistry(
            context,
            new TenantScopeBypass(NullLogger<TenantScopeBypass>.Instance),
            NullLogger<TenantRegistry>.Instance);
    }
}