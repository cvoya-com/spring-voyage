// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ParticipantDisplayNameResolver"/>. Covers
/// every scheme branch (<c>agent</c>, <c>unit</c>, <c>human</c>,
/// <c>connector</c>, <c>tenant-user</c>) and the fallback contract from
/// #2532 / #2533: unknown schemes and missing rows never leak a raw
/// GUID. Display-name resolution for connectors falls back to the
/// catalog kind when the row has no display name, then to the generic
/// "a connector" string when the row is missing.
/// </summary>
public class ParticipantDisplayNameResolverTests : IDisposable
{
    private static readonly Guid TenantId = new("11111111-2222-3333-4444-000000000777");
    private readonly DbContextOptions<SpringDbContext> _dbOptions;
    private readonly StaticTenantContext _tenantContext;
    private readonly SpringDbContext _db;
    private readonly IHumanIdentityResolver _humans = Substitute.For<IHumanIdentityResolver>();
    private readonly ParticipantDisplayNameResolver _resolver;

    public ParticipantDisplayNameResolverTests()
    {
        _dbOptions = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _tenantContext = new StaticTenantContext(TenantId);
        _db = new SpringDbContext(_dbOptions, _tenantContext);
        _resolver = new ParticipantDisplayNameResolver(
            _db, _humans, NullLogger<ParticipantDisplayNameResolver>.Instance);
    }

    [Fact]
    public async Task ResolveAsync_ConnectorWithDisplayName_ReturnsDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedConnectorAsync(id, type: "github", displayName: "Spring's GitHub");

        var name = await _resolver.ResolveAsync(FormatAddress(Address.ConnectorScheme, id), ct);

        name.ShouldBe("Spring's GitHub");
    }

    [Fact]
    public async Task ResolveAsync_ConnectorWithEmptyDisplayName_ReturnsKindFallback()
    {
        // The connector row exists but has no display name (operator
        // never named it). The fallback surfaces the catalog kind so
        // the engagement list reads "a github connector" rather than
        // the generic "a connector".
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedConnectorAsync(id, type: "github", displayName: string.Empty);

        var name = await _resolver.ResolveAsync(FormatAddress(Address.ConnectorScheme, id), ct);

        name.ShouldBe("a github connector");
    }

    [Fact]
    public async Task ResolveStatusAsync_ConnectorWithEmptyDisplayName_IsFlaggedAsFallback()
    {
        // The kind-aware fallback ("a github connector") is still a
        // fallback — snapshot-aware callers must be able to prefer a
        // previously-captured real name over the kind label.
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedConnectorAsync(id, type: "github", displayName: string.Empty);

        var status = await _resolver.ResolveStatusAsync(FormatAddress(Address.ConnectorScheme, id), ct);

        status.DisplayName.ShouldBe("a github connector");
        status.IsFallback.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_MissingConnector_ReturnsGenericConnectorFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();

        var name = await _resolver.ResolveAsync(FormatAddress(Address.ConnectorScheme, id), ct);

        name.ShouldBe("a connector");
    }

    [Fact]
    public async Task ResolveAsync_SoftDeletedConnector_ReturnsGenericConnectorFallback()
    {
        // Soft-deleted rows are filtered by the DbContext query filter,
        // so the resolver sees no row and falls through to the generic
        // fallback — the same path as a missing row.
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedConnectorAsync(id, type: "github", displayName: "Spring's GitHub", deletedAt: DateTimeOffset.UtcNow);

        var name = await _resolver.ResolveAsync(FormatAddress(Address.ConnectorScheme, id), ct);

        name.ShouldBe("a connector");
    }

    [Fact]
    public async Task ResolveAsync_MissingAgent_ReturnsAgentFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = await _resolver.ResolveAsync(FormatAddress(Address.AgentScheme, Guid.NewGuid()), ct);
        name.ShouldBe("an agent");
    }

    [Fact]
    public async Task ResolveAsync_MissingUnit_ReturnsUnitFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = await _resolver.ResolveAsync(FormatAddress(Address.UnitScheme, Guid.NewGuid()), ct);
        name.ShouldBe("a unit");
    }

    [Fact]
    public async Task ResolveAsync_MissingHuman_ReturnsHumanFallback()
    {
        // The HumanIdentityResolver returns null for unknown ids; the
        // participant resolver translates that into the per-scheme
        // generic "someone" rather than the old <deleted> sentinel.
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        _humans.GetDisplayNameAsync(id, ct).Returns(Task.FromResult<string?>(null));

        var name = await _resolver.ResolveAsync(FormatAddress(Address.HumanScheme, id), ct);

        name.ShouldBe("someone");
    }

    [Fact]
    public async Task ResolveAsync_MissingTenantUser_ReturnsMemberFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = await _resolver.ResolveAsync(FormatAddress(Address.TenantUserScheme, Guid.NewGuid()), ct);
        name.ShouldBe("a member");
    }

    [Fact]
    public async Task ResolveAsync_UnknownScheme_ReturnsSchemeFallbackNeverGuid()
    {
        // The pre-#2532 behaviour returned the raw hex id when the
        // scheme had no case — that's how connector ids leaked. The new
        // fallback surfaces "a <scheme>" instead so operators see
        // *something* meaningful and never a 32-hex literal.
        var ct = TestContext.Current.CancellationToken;
        var hexId = GuidFormatter.Format(Guid.NewGuid());

        var name = await _resolver.ResolveAsync($"weird:{hexId}", ct);

        name.ShouldBe("a weird");
        name.ShouldNotContain(hexId);
    }

    [Fact]
    public async Task ResolveAsync_MalformedAddress_ReturnsAddressVerbatim()
    {
        // Truly malformed input — no scheme separator at all. The
        // resolver returns the raw value so logs / debugging surfaces
        // still carry the input. ResolveStatusAsync flags this as a
        // fallback because there is no entity behind it.
        var ct = TestContext.Current.CancellationToken;
        var status = await _resolver.ResolveStatusAsync("garbage-no-scheme", ct);

        status.DisplayName.ShouldBe("garbage-no-scheme");
        status.IsFallback.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_AgentRowFound_ReturnsRealDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedAgentAsync(id, "Ada Lovelace");

        var status = await _resolver.ResolveStatusAsync(FormatAddress(Address.AgentScheme, id), ct);

        status.DisplayName.ShouldBe("Ada Lovelace");
        status.IsFallback.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_CachesResultsWithinTheRequest()
    {
        // The resolver caches per-request so repeat calls don't hit the
        // database. We seed the row, resolve once, then delete the row
        // out-of-band and resolve again — if the cache works the second
        // call still returns the seeded name.
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedAgentAsync(id, "Cached Name");

        var first = await _resolver.ResolveAsync(FormatAddress(Address.AgentScheme, id), ct);
        first.ShouldBe("Cached Name");

        // Remove the row from the DB directly; the cache should still
        // return the original name.
        var row = await _db.AgentDefinitions.IgnoreQueryFilters().FirstAsync(a => a.Id == id, ct);
        _db.AgentDefinitions.Remove(row);
        await _db.SaveChangesAsync(ct);

        var second = await _resolver.ResolveAsync(FormatAddress(Address.AgentScheme, id), ct);
        second.ShouldBe("Cached Name");
    }

    [Fact]
    public async Task ResolveAsync_EmptyAddress_ReturnsGenericFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var status = await _resolver.ResolveStatusAsync(string.Empty, ct);
        status.DisplayName.ShouldNotBeNullOrWhiteSpace();
        status.IsFallback.ShouldBeTrue();
    }

    // -------------------------------------------------------------------
    // IsDeletedAsync (#2732) — drives auto-archive of orphaned engagements.
    // -------------------------------------------------------------------

    [Fact]
    public async Task IsDeletedAsync_LiveAgent_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedAgentAsync(id, "Ada Lovelace");

        var deleted = await _resolver.IsDeletedAsync(FormatAddress(Address.AgentScheme, id), ct);

        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task IsDeletedAsync_SoftDeletedAgent_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedAgentAsync(id, "Ada Lovelace", deletedAt: DateTimeOffset.UtcNow);

        var deleted = await _resolver.IsDeletedAsync(FormatAddress(Address.AgentScheme, id), ct);

        deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task IsDeletedAsync_MissingAgent_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;

        var deleted = await _resolver.IsDeletedAsync(
            FormatAddress(Address.AgentScheme, Guid.NewGuid()), ct);

        deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task IsDeletedAsync_LiveUnit_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedUnitAsync(id, "Engineering");

        var deleted = await _resolver.IsDeletedAsync(FormatAddress(Address.UnitScheme, id), ct);

        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task IsDeletedAsync_SoftDeletedUnit_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedUnitAsync(id, "Engineering", deletedAt: DateTimeOffset.UtcNow);

        var deleted = await _resolver.IsDeletedAsync(FormatAddress(Address.UnitScheme, id), ct);

        deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task IsDeletedAsync_MissingUnit_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;

        var deleted = await _resolver.IsDeletedAsync(
            FormatAddress(Address.UnitScheme, Guid.NewGuid()), ct);

        deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task IsDeletedAsync_LiveConnector_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedConnectorAsync(id, type: "github", displayName: "Spring's GitHub");

        var deleted = await _resolver.IsDeletedAsync(
            FormatAddress(Address.ConnectorScheme, id), ct);

        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task IsDeletedAsync_SoftDeletedConnector_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedConnectorAsync(id, type: "github", displayName: "Spring's GitHub",
            deletedAt: DateTimeOffset.UtcNow);

        var deleted = await _resolver.IsDeletedAsync(
            FormatAddress(Address.ConnectorScheme, id), ct);

        deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task IsDeletedAsync_MissingConnector_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;

        var deleted = await _resolver.IsDeletedAsync(
            FormatAddress(Address.ConnectorScheme, Guid.NewGuid()), ct);

        deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task IsDeletedAsync_LiveTenantUser_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedTenantUserAsync(id, "Savas");

        var deleted = await _resolver.IsDeletedAsync(
            FormatAddress(Address.TenantUserScheme, id), ct);

        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task IsDeletedAsync_MissingTenantUser_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;

        var deleted = await _resolver.IsDeletedAsync(
            FormatAddress(Address.TenantUserScheme, Guid.NewGuid()), ct);

        deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task IsDeletedAsync_Human_AlwaysReturnsFalse()
    {
        // Humans are not archivable per the orphan rule — they are
        // always live participants on their own engagements. No DB
        // lookup is performed (verified by the test asserting against
        // a never-seeded id, which would otherwise show as missing).
        var ct = TestContext.Current.CancellationToken;

        var deleted = await _resolver.IsDeletedAsync(
            FormatAddress(Address.HumanScheme, Guid.NewGuid()), ct);

        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task IsDeletedAsync_UnknownScheme_ReturnsFalse()
    {
        // Conservative fallback: a scheme the resolver cannot reason
        // about should not cause a thread to be archived.
        var ct = TestContext.Current.CancellationToken;
        var hexId = GuidFormatter.Format(Guid.NewGuid());

        var deleted = await _resolver.IsDeletedAsync($"weird:{hexId}", ct);

        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task IsDeletedAsync_MalformedAddress_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;

        var deleted = await _resolver.IsDeletedAsync("garbage-no-scheme", ct);

        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task IsDeletedAsync_EmptyAddress_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;

        var deleted = await _resolver.IsDeletedAsync(string.Empty, ct);

        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task IsDeletedAsync_CachesResultsWithinTheRequest()
    {
        // Mirror ResolveAsync's per-request caching: seed live, resolve
        // once, hard-delete the row, resolve again — the second call
        // should still see the cached not-deleted answer.
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await SeedAgentAsync(id, "Cached Agent");
        var address = FormatAddress(Address.AgentScheme, id);

        var first = await _resolver.IsDeletedAsync(address, ct);
        first.ShouldBeFalse();

        var row = await _db.AgentDefinitions.IgnoreQueryFilters().FirstAsync(a => a.Id == id, ct);
        _db.AgentDefinitions.Remove(row);
        await _db.SaveChangesAsync(ct);

        var second = await _resolver.IsDeletedAsync(address, ct);
        second.ShouldBeFalse(); // still cached as not-deleted
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string FormatAddress(string scheme, Guid id) =>
        $"{scheme}:{GuidFormatter.Format(id)}";

    private async Task SeedConnectorAsync(
        Guid id,
        string type,
        string displayName,
        DateTimeOffset? deletedAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        _db.ConnectorDefinitions.Add(new ConnectorDefinitionEntity
        {
            Id = id,
            TenantId = TenantId,
            Type = type,
            DisplayName = displayName,
            ToolNamespace = type,
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deletedAt,
        });
        await _db.SaveChangesAsync();
    }

    private async Task SeedAgentAsync(Guid id, string displayName, DateTimeOffset? deletedAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        _db.AgentDefinitions.Add(new AgentDefinitionEntity
        {
            Id = id,
            TenantId = TenantId,
            DisplayName = displayName,
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deletedAt,
        });
        await _db.SaveChangesAsync();
    }

    private async Task SeedUnitAsync(Guid id, string displayName, DateTimeOffset? deletedAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        _db.UnitDefinitions.Add(new UnitDefinitionEntity
        {
            Id = id,
            TenantId = TenantId,
            DisplayName = displayName,
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deletedAt,
        });
        await _db.SaveChangesAsync();
    }

    private async Task SeedTenantUserAsync(Guid id, string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        _db.TenantUsers.Add(new TenantUserEntity
        {
            Id = id,
            TenantId = TenantId,
            DisplayName = displayName,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync();
    }
}
