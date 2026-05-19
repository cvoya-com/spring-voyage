// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests <see cref="TenantUserConnectorIdentityWriter"/> (ADR-0047 §13).
/// Mirrors the upsert / 404 / 409 shape of
/// <c>TenantUserIdentityEndpoints.UpsertIdentityAsync</c> so the OAuth
/// callback's user-identity refresh converges on the same semantics
/// the HTTP endpoint emits.
/// </summary>
public class TenantUserConnectorIdentityWriterTests : IDisposable
{
    private static readonly Guid TenantA = new("aaaa0000-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = new("bbbb0000-0000-0000-0000-000000000002");
    private static readonly Guid TenantUser1 = new("cccc0000-0000-0000-0000-000000000003");
    private static readonly Guid TenantUser2 = new("cccc0000-0000-0000-0000-000000000004");

    private readonly string _dbName = Guid.NewGuid().ToString();

    public void Dispose() => GC.SuppressFinalize(this);

    private SpringDbContext CreateContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        return new SpringDbContext(options, new StaticTenantContext(tenantId));
    }

    [Fact]
    public async Task UpsertAsync_NewRow_Inserts()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var seed = CreateContext(TenantA))
        {
            seed.TenantUsers.Add(new TenantUserEntity
            {
                Id = TenantUser1,
                TenantId = TenantA,
                DisplayName = "Alice",
            });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = CreateContext(TenantA);
        var writer = new TenantUserConnectorIdentityWriter(ctx);
        var outcome = await writer.UpsertAsync(
            TenantUser1, "github", "alice", "Alice McCoder", ct);

        outcome.ShouldBe(TenantUserConnectorIdentityUpsertOutcome.Upserted);

        var row = await ctx.TenantUserConnectorIdentities
            .AsNoTracking()
            .SingleAsync(e => e.TenantUserId == TenantUser1, ct);
        row.Username.ShouldBe("alice");
        row.DisplayHandle.ShouldBe("Alice McCoder");
    }

    [Fact]
    public async Task UpsertAsync_ExistingPair_UpdatesInPlace()
    {
        // ADR-0047 §2 natural key: (tenant, tenant_user, connector). A
        // re-upsert with a different username on the same connector
        // replaces the row in place — the OAuth callback's identity-
        // refresh path relies on this so the operator-friendly UX is
        // "re-link and your handle updates".
        var ct = TestContext.Current.CancellationToken;
        using (var seed = CreateContext(TenantA))
        {
            seed.TenantUsers.Add(new TenantUserEntity
            {
                Id = TenantUser1,
                TenantId = TenantA,
                DisplayName = "Alice",
            });
            seed.TenantUserConnectorIdentities.Add(new TenantUserConnectorIdentityEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TenantA,
                TenantUserId = TenantUser1,
                ConnectorId = "github",
                Username = "old-login",
                DisplayHandle = "old",
            });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = CreateContext(TenantA);
        var writer = new TenantUserConnectorIdentityWriter(ctx);
        var outcome = await writer.UpsertAsync(
            TenantUser1, "github", "new-login", "new", ct);

        outcome.ShouldBe(TenantUserConnectorIdentityUpsertOutcome.Upserted);

        var rows = await ctx.TenantUserConnectorIdentities
            .AsNoTracking()
            .Where(e => e.TenantUserId == TenantUser1 && e.ConnectorId == "github")
            .ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].Username.ShouldBe("new-login");
        rows[0].DisplayHandle.ShouldBe("new");
    }

    [Fact]
    public async Task UpsertAsync_UnknownTenantUser_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = CreateContext(TenantA);
        var writer = new TenantUserConnectorIdentityWriter(ctx);

        var outcome = await writer.UpsertAsync(
            Guid.NewGuid(), "github", "alice", null, ct);

        outcome.ShouldBe(TenantUserConnectorIdentityUpsertOutcome.TenantUserNotFound);
    }

    [Fact]
    public async Task UpsertAsync_GuidEmptyTenantUser_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = CreateContext(TenantA);
        var writer = new TenantUserConnectorIdentityWriter(ctx);

        var outcome = await writer.UpsertAsync(
            Guid.Empty, "github", "alice", null, ct);

        outcome.ShouldBe(TenantUserConnectorIdentityUpsertOutcome.TenantUserNotFound);
    }

    [Fact]
    public async Task UpsertAsync_LoginClaimedByOtherTenantUser_ReturnsConflict()
    {
        // ADR-0047 §2 reverse-lookup unique index: one connector login
        // maps to at most one tenant user per tenant. The OAuth callback
        // surfaces this as a soft warning — the persister still keeps
        // the secret-write side effect.
        var ct = TestContext.Current.CancellationToken;
        using (var seed = CreateContext(TenantA))
        {
            seed.TenantUsers.Add(new TenantUserEntity
            {
                Id = TenantUser1,
                TenantId = TenantA,
                DisplayName = "Alice",
            });
            seed.TenantUsers.Add(new TenantUserEntity
            {
                Id = TenantUser2,
                TenantId = TenantA,
                DisplayName = "Bob",
            });
            seed.TenantUserConnectorIdentities.Add(new TenantUserConnectorIdentityEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TenantA,
                TenantUserId = TenantUser1,
                ConnectorId = "github",
                Username = "shared-login",
            });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = CreateContext(TenantA);
        var writer = new TenantUserConnectorIdentityWriter(ctx);
        var outcome = await writer.UpsertAsync(
            TenantUser2, "github", "shared-login", null, ct);

        outcome.ShouldBe(TenantUserConnectorIdentityUpsertOutcome.LoginAlreadyClaimed);
    }

    [Fact]
    public async Task UpsertAsync_TenantUserInDifferentTenant_ReturnsNotFound()
    {
        // The DbContext's tenant query filter scopes the existence
        // probe; addressing a tenant user that lives in tenant B from a
        // tenant A writer must not silently succeed.
        var ct = TestContext.Current.CancellationToken;
        using (var seed = CreateContext(TenantB))
        {
            seed.TenantUsers.Add(new TenantUserEntity
            {
                Id = TenantUser1,
                TenantId = TenantB,
                DisplayName = "Cross-tenant Alice",
            });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = CreateContext(TenantA);
        var writer = new TenantUserConnectorIdentityWriter(ctx);
        var outcome = await writer.UpsertAsync(
            TenantUser1, "github", "alice", null, ct);

        outcome.ShouldBe(TenantUserConnectorIdentityUpsertOutcome.TenantUserNotFound);
    }

    [Fact]
    public async Task UpsertAsync_BlankInputs_ReturnsUpsertedNoOp()
    {
        // Defensive branch — the OAuth callback supplies these from the
        // GitHub user-info response so a blank login should never reach
        // the writer in practice. When it does, fall through quietly
        // (no row mutation) rather than rewriting the row with empty
        // values.
        var ct = TestContext.Current.CancellationToken;
        using (var seed = CreateContext(TenantA))
        {
            seed.TenantUsers.Add(new TenantUserEntity
            {
                Id = TenantUser1,
                TenantId = TenantA,
                DisplayName = "Alice",
            });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = CreateContext(TenantA);
        var writer = new TenantUserConnectorIdentityWriter(ctx);

        (await writer.UpsertAsync(TenantUser1, "", "alice", null, ct))
            .ShouldBe(TenantUserConnectorIdentityUpsertOutcome.Upserted);
        (await writer.UpsertAsync(TenantUser1, "github", "   ", null, ct))
            .ShouldBe(TenantUserConnectorIdentityUpsertOutcome.Upserted);

        // Neither path mutated the row.
        (await ctx.TenantUserConnectorIdentities.AnyAsync(ct)).ShouldBeFalse();
    }
}
