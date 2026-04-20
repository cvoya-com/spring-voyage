// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DefaultTenantSkillBundleBindingService"/>.
/// Covers bind/upsert semantics, list + get round-trips, idempotency,
/// and tenant isolation.
/// </summary>
public class DefaultTenantSkillBundleBindingServiceTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [Fact]
    public async Task BindAsync_CreatesRowOnFirstCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut(Guid.NewGuid().ToString(), TenantA);

        var binding = await sut.BindAsync("software-engineering", enabled: true, ct);

        binding.TenantId.ShouldBe(TenantA);
        binding.BundleId.ShouldBe("software-engineering");
        binding.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task BindAsync_IdempotentOnSameEnabledValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut(Guid.NewGuid().ToString(), TenantA);

        var first = await sut.BindAsync("research", enabled: true, ct);
        var second = await sut.BindAsync("research", enabled: true, ct);

        second.BoundAt.ShouldBe(first.BoundAt);
    }

    [Fact]
    public async Task BindAsync_FlipsEnabledWithoutRewritingBoundAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut(Guid.NewGuid().ToString(), TenantA);

        var first = await sut.BindAsync("research", enabled: true, ct);
        var flipped = await sut.BindAsync("research", enabled: false, ct);

        flipped.Enabled.ShouldBeFalse();
        flipped.BoundAt.ShouldBe(first.BoundAt);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotBound()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut(Guid.NewGuid().ToString(), TenantA);

        (await sut.GetAsync("nothing", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_HonoursTenantIsolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var sutA = CreateSut(dbName, TenantA);
        var sutB = CreateSut(dbName, TenantB);

        await sutA.BindAsync("software-engineering", enabled: true, ct);
        await sutB.BindAsync("research", enabled: true, ct);

        (await sutA.ListAsync(ct)).Select(b => b.BundleId).ShouldBe(new[] { "software-engineering" });
        (await sutB.ListAsync(ct)).Select(b => b.BundleId).ShouldBe(new[] { "research" });
    }

    private static DefaultTenantSkillBundleBindingService CreateSut(string dbName, string tenantId)
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        var context = new SpringDbContext(options, new StaticTenantContext(tenantId));
        return new DefaultTenantSkillBundleBindingService(
            context,
            new StaticTenantContext(tenantId),
            NullLogger<DefaultTenantSkillBundleBindingService>.Instance);
    }
}