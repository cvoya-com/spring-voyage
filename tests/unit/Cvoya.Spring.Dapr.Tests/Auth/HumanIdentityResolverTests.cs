// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using System;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="HumanIdentityResolver"/>. Focused on the
/// upsert path's <c>DisplayName</c> derivation introduced by #2860: when
/// the caller does not supply a display name, the resolver fills it from
/// the bound TenantUser's <c>DisplayName</c> (single source of truth) so
/// the auth-time default Hat matches the prefix package-minted Hats use.
/// </summary>
public class HumanIdentityResolverTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-3333-4444-000000000abc");

    private readonly DbContextOptions<SpringDbContext> _dbOptions;
    private readonly StaticTenantContext _tenantContext;
    private readonly SpringDbContext _db;

    public HumanIdentityResolverTests()
    {
        _dbOptions = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _tenantContext = new StaticTenantContext(TenantId);
        _db = new SpringDbContext(_dbOptions, _tenantContext);
    }

    private HumanIdentityResolver CreateResolver() =>
        new(
            _db,
            new OssTenantUserDefaultResolver(),
            NullLogger<HumanIdentityResolver>.Instance);

    private async Task SeedTenantUserAsync(Guid id, string displayName)
    {
        _db.TenantUsers.Add(new TenantUserEntity
        {
            Id = id,
            TenantId = TenantId,
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task ResolveByUsernameAsync_NewRow_NoDisplayName_UsesTenantUserDisplayName()
    {
        // #2860: the operator's seeded TenantUser is the single source of
        // truth for the default Hat's prefix. When the auth path calls
        // ResolveByUsernameAsync without a displayName (post-fix the
        // LocalDevAuthHandler no longer stamps a literal Claim), the
        // resolver must look up the TenantUser DisplayName and use it.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Operator");
        var resolver = CreateResolver();

        var id = await resolver.ResolveByUsernameAsync("local-dev-user", displayName: null, ct);

        id.ShouldNotBe(Guid.Empty);
        var row = await _db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct);
        row.ShouldNotBeNull();
        row!.DisplayName.ShouldBe("Operator");
        row.TenantUserId.ShouldBe(OssTenantUserIds.Operator);
    }

    [Fact]
    public async Task ResolveByUsernameAsync_NewRow_RenamedTenantUser_PrefixFollows()
    {
        // Renaming the TenantUser updates the prefix on subsequent Hat
        // mints (per the issue's acceptance criterion). Verified by
        // seeding "Foo" instead of "Operator" — the auto-minted Hat
        // should read "Foo".
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Foo");
        var resolver = CreateResolver();

        var id = await resolver.ResolveByUsernameAsync("local-dev-user", displayName: null, ct);

        var row = await _db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct);
        row!.DisplayName.ShouldBe("Foo");
    }

    [Fact]
    public async Task ResolveByUsernameAsync_NewRow_ExplicitDisplayName_WinsOverTenantUserLookup()
    {
        // Callers that *do* know the display name (cloud overlay's JWT
        // path) still take precedence. Verifies the TenantUser-lookup
        // fallback only runs when no name was supplied.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Operator");
        var resolver = CreateResolver();

        var id = await resolver.ResolveByUsernameAsync("alice@example.com", "Alice Anderson", ct);

        var row = await _db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct);
        row!.DisplayName.ShouldBe("Alice Anderson");
    }

    [Fact]
    public async Task ResolveByUsernameAsync_NewRow_TenantUserMissing_FallsBackToUsername()
    {
        // Defensive guard: when the TenantUser row hasn't been seeded
        // (malformed state — production seeder always runs first), the
        // resolver still emits a non-empty DisplayName by falling back
        // to the username rather than crashing or persisting empty.
        var ct = TestContext.Current.CancellationToken;
        var resolver = CreateResolver();

        var id = await resolver.ResolveByUsernameAsync("orphan-user", displayName: null, ct);

        var row = await _db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct);
        row!.DisplayName.ShouldBe("orphan-user");
    }

    [Fact]
    public async Task ResolveByUsernameAsync_ExistingRow_ReturnsCachedId_NoOverwrite()
    {
        // Once a Hat row exists for a username, subsequent calls return
        // the same id and never overwrite the DisplayName — even when a
        // newer Claim would imply a different name. The lookup is the
        // source of truth post-creation.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Operator");
        var resolver = CreateResolver();

        var first = await resolver.ResolveByUsernameAsync("local-dev-user", displayName: null, ct);
        var second = await resolver.ResolveByUsernameAsync("local-dev-user", "Some Other Name", ct);

        first.ShouldBe(second);
        var row = await _db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == first, ct);
        row!.DisplayName.ShouldBe("Operator");
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
