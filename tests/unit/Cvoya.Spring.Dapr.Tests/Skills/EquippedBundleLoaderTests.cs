// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Collections.Generic;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Unit coverage for <see cref="EquippedBundleLoader"/>, the helper
/// invoked from <c>AgentActor.LoadEquippedBundlesAsync</c>. Exercises
/// every leg of the unit-as-agent vs leaf-agent vs multi-parent
/// dispatch (#2360 + #2363):
/// <list type="bullet">
///   <item><description>scope-factory absent → <c>(null, null)</c>;</description></item>
///   <item><description>leaf agent / single parent unit;</description></item>
///   <item><description>leaf agent / multiple parents, alphabetical-by-display-name tie-break;</description></item>
///   <item><description>multi-parent dedup on <c>(package, skill)</c>;</description></item>
///   <item><description>unit-as-agent direct-wins over an inherited bundle of the same coordinates;</description></item>
///   <item><description>empty / no memberships → no inherited bundles, agent-store Layer 4 still flows.</description></item>
/// </list>
/// </summary>
public sealed class EquippedBundleLoaderTests : IDisposable
{
    // Static UUIDs let assertions read declaratively.
    private static readonly Guid AgentId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UnitAlpha = new("bbbbbbbb-0000-0000-0000-0000000000a1");
    private static readonly Guid UnitBeta = new("bbbbbbbb-0000-0000-0000-0000000000b1");
    private static readonly Guid UnitOrphan = new("bbbbbbbb-0000-0000-0000-0000000000c1");

    private readonly ServiceProvider _serviceProvider;
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly StubUnitSkillBundleStore _unitStore = new();
    private readonly StubAgentSkillBundleStore _agentStore = new();

    public EquippedBundleLoaderTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(OssTenantIds.Default));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddScoped<IUnitMembershipRepository, UnitMembershipRepository>();
        services.AddSingleton<IUnitSkillBundleStore>(_unitStore);
        services.AddSingleton<IAgentSkillBundleStore>(_agentStore);
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    private IServiceScopeFactory ScopeFactory =>
        _serviceProvider.GetRequiredService<IServiceScopeFactory>();

    private SpringDbContext NewContext()
    {
        var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.Database.EnsureCreated();
        return db;
    }

    private static SkillBundle Bundle(string pkg, string skill, string? body = null) =>
        new(pkg, skill, body ?? $"## {pkg}/{skill}\nbody", Array.Empty<SkillToolRequirement>());

    // ---------------------------------------------------------------
    // Scope-factory + plumbing
    // ---------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_NoScopeFactory_ReturnsNullPair()
    {
        var ct = TestContext.Current.CancellationToken;

        var (unit, agent) = await EquippedBundleLoader.LoadAsync(
            scopeFactory: null,
            actorId: GuidFormatter.Format(AgentId),
            cancellationToken: ct);

        unit.ShouldBeNull();
        agent.ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_NoBundlesAnywhere_ReturnsNullPair()
    {
        var ct = TestContext.Current.CancellationToken;

        var (unit, agent) = await EquippedBundleLoader.LoadAsync(
            ScopeFactory,
            actorId: GuidFormatter.Format(AgentId),
            cancellationToken: ct);

        unit.ShouldBeNull();
        agent.ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_AgentStoreOnly_FeedsLayer4()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentKey = GuidFormatter.Format(AgentId);

        _agentStore.Set(agentKey, new[] { Bundle("pkg", "agent-skill") });

        var (unit, agent) = await EquippedBundleLoader.LoadAsync(
            ScopeFactory, agentKey, ct);

        unit.ShouldBeNull();
        agent.ShouldNotBeNull();
        agent.Count.ShouldBe(1);
        agent[0].SkillName.ShouldBe("agent-skill");
    }

    // ---------------------------------------------------------------
    // Unit-as-agent (ADR-0039) — direct keyed read
    // ---------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_UnitAsAgent_ReadsOwnUnitBundlesDirectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitKey = GuidFormatter.Format(UnitAlpha);

        // No memberships -> the only contribution is the unit's own entry.
        _unitStore.Set(unitKey, new[] { Bundle("pkg", "unit-skill") });

        var (unit, agent) = await EquippedBundleLoader.LoadAsync(
            ScopeFactory, unitKey, ct);

        agent.ShouldBeNull();
        unit.ShouldNotBeNull();
        unit.Count.ShouldBe(1);
        unit[0].SkillName.ShouldBe("unit-skill");
    }

    // ---------------------------------------------------------------
    // Leaf-agent inheritance (#2363) — single parent
    // ---------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_LeafAgentSingleParent_PicksUpParentBundles()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentKey = GuidFormatter.Format(AgentId);

        using (var db = NewContext())
        {
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = UnitAlpha,
                DisplayName = "Alpha Unit",
            });
            db.UnitMemberships.Add(new UnitMembershipEntity
            {
                UnitId = UnitAlpha,
                AgentId = AgentId,
                Enabled = true,
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        _unitStore.Set(GuidFormatter.Format(UnitAlpha),
            new[] { Bundle("pkg", "alpha-skill") });

        var (unit, agent) = await EquippedBundleLoader.LoadAsync(
            ScopeFactory, agentKey, ct);

        agent.ShouldBeNull();
        unit.ShouldNotBeNull();
        unit.Count.ShouldBe(1);
        unit[0].SkillName.ShouldBe("alpha-skill");
    }

    // ---------------------------------------------------------------
    // Leaf-agent inheritance — multi-parent alphabetical tie-break
    // ---------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_LeafAgentMultiParent_OrderedByDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentKey = GuidFormatter.Format(AgentId);

        using (var db = NewContext())
        {
            // Insert Beta first (later display name) and Alpha second to
            // prove the loader sorts by display name rather than insertion
            // / CreatedAt order.
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = UnitBeta,
                DisplayName = "Beta Unit",
            });
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = UnitAlpha,
                DisplayName = "Alpha Unit",
            });
            db.UnitMemberships.Add(new UnitMembershipEntity
            {
                UnitId = UnitBeta,
                AgentId = AgentId,
                Enabled = true,
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.UnitMemberships.Add(new UnitMembershipEntity
            {
                UnitId = UnitAlpha,
                AgentId = AgentId,
                Enabled = true,
                IsPrimary = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        _unitStore.Set(GuidFormatter.Format(UnitAlpha),
            new[] { Bundle("pkg", "from-alpha") });
        _unitStore.Set(GuidFormatter.Format(UnitBeta),
            new[] { Bundle("pkg", "from-beta") });

        var (unit, _) = await EquippedBundleLoader.LoadAsync(
            ScopeFactory, agentKey, ct);

        unit.ShouldNotBeNull();
        unit.Select(b => b.SkillName).ShouldBe(new[] { "from-alpha", "from-beta" });
    }

    // ---------------------------------------------------------------
    // Leaf-agent inheritance — dedup on (package, skill)
    // ---------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_MultiParentOverlap_DedupedFirstWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentKey = GuidFormatter.Format(AgentId);

        using (var db = NewContext())
        {
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = UnitAlpha,
                DisplayName = "Alpha Unit",
            });
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = UnitBeta,
                DisplayName = "Beta Unit",
            });
            db.UnitMemberships.Add(new UnitMembershipEntity
            {
                UnitId = UnitAlpha,
                AgentId = AgentId,
                Enabled = true,
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.UnitMemberships.Add(new UnitMembershipEntity
            {
                UnitId = UnitBeta,
                AgentId = AgentId,
                Enabled = true,
                IsPrimary = false,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        // Both parents equip the same (pkg, shared) — Alpha sorts first
        // by display name, so Alpha's prompt body wins.
        _unitStore.Set(GuidFormatter.Format(UnitAlpha),
            new[] { Bundle("pkg", "shared", "ALPHA-BODY") });
        _unitStore.Set(GuidFormatter.Format(UnitBeta),
            new[] { Bundle("pkg", "shared", "BETA-BODY") });

        var (unit, _) = await EquippedBundleLoader.LoadAsync(
            ScopeFactory, agentKey, ct);

        unit.ShouldNotBeNull();
        unit.Count.ShouldBe(1);
        unit[0].Prompt.ShouldContain("ALPHA-BODY");
        unit[0].Prompt.ShouldNotContain("BETA-BODY");
    }

    // ---------------------------------------------------------------
    // Unit-as-agent direct-wins over inherited duplicate (sub-unit not
    // in scope at v0.1, but the dedup gate still has to behave when
    // the same coordinates appear on the unit's own entry and on a
    // parent's entry — a sub-unit relationship not modelled here would
    // be the only way to hit this. We exercise the gate by faking a
    // parent membership on a unit subject — the membership table is
    // agent→unit so this is artificial, but it locks the direct-wins
    // ordering invariant for the cascading-inheritance work that may
    // follow).
    // ---------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_DirectEntryWinsOverInheritedDuplicate()
    {
        var ct = TestContext.Current.CancellationToken;

        // Subject is the leaf agent. The agent's OWN bundles entry is a
        // no-op against IUnitSkillBundleStore (the agent's id is never a
        // unit-store key in practice), but we can simulate the
        // "direct-key collides with an inherited row" branch by writing
        // to the agent's id slot in the stub store. This pins the
        // first-write-wins invariant the helper relies on for future
        // cascading-inheritance behaviour.
        var agentKey = GuidFormatter.Format(AgentId);

        using (var db = NewContext())
        {
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = UnitAlpha,
                DisplayName = "Alpha Unit",
            });
            db.UnitMemberships.Add(new UnitMembershipEntity
            {
                UnitId = UnitAlpha,
                AgentId = AgentId,
                Enabled = true,
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        _unitStore.Set(agentKey,
            new[] { Bundle("pkg", "shared", "DIRECT-BODY") });
        _unitStore.Set(GuidFormatter.Format(UnitAlpha),
            new[] { Bundle("pkg", "shared", "INHERITED-BODY") });

        var (unit, _) = await EquippedBundleLoader.LoadAsync(
            ScopeFactory, agentKey, ct);

        unit.ShouldNotBeNull();
        unit.Count.ShouldBe(1);
        unit[0].Prompt.ShouldContain("DIRECT-BODY");
    }

    // ---------------------------------------------------------------
    // Membership row pointing at a unit with no entry in the bundle
    // store — silently skipped, no exception.
    // ---------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_ParentWithoutBundles_Skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentKey = GuidFormatter.Format(AgentId);

        using (var db = NewContext())
        {
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = UnitOrphan,
                DisplayName = "Orphan Unit",
            });
            db.UnitMemberships.Add(new UnitMembershipEntity
            {
                UnitId = UnitOrphan,
                AgentId = AgentId,
                Enabled = true,
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        // Note: no _unitStore.Set for UnitOrphan.

        var (unit, _) = await EquippedBundleLoader.LoadAsync(
            ScopeFactory, agentKey, ct);

        unit.ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_AgentLayer4AndUnitLayer2_BothRender()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentKey = GuidFormatter.Format(AgentId);

        using (var db = NewContext())
        {
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = UnitAlpha,
                DisplayName = "Alpha Unit",
            });
            db.UnitMemberships.Add(new UnitMembershipEntity
            {
                UnitId = UnitAlpha,
                AgentId = AgentId,
                Enabled = true,
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        _unitStore.Set(GuidFormatter.Format(UnitAlpha),
            new[] { Bundle("pkg", "unit-skill") });
        _agentStore.Set(agentKey,
            new[] { Bundle("pkg", "agent-skill") });

        var (unit, agent) = await EquippedBundleLoader.LoadAsync(
            ScopeFactory, agentKey, ct);

        unit.ShouldNotBeNull();
        unit.Count.ShouldBe(1);
        unit[0].SkillName.ShouldBe("unit-skill");
        agent.ShouldNotBeNull();
        agent.Count.ShouldBe(1);
        agent[0].SkillName.ShouldBe("agent-skill");
    }

    // -----------------------------------------------------------------
    // Minimal in-memory stubs for the bundle stores. The production
    // stores hit IStateStore + the resolver pipeline; for the loader's
    // contract we only need a faithful in-memory map.
    // -----------------------------------------------------------------

    private sealed class StubUnitSkillBundleStore : IUnitSkillBundleStore
    {
        private readonly Dictionary<string, IReadOnlyList<SkillBundle>> _byId = new(StringComparer.Ordinal);

        public void Set(string id, IReadOnlyList<SkillBundle> bundles) => _byId[id] = bundles;

        public Task<IReadOnlyList<SkillBundle>> GetAsync(string unitId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SkillBundle>>(
                _byId.TryGetValue(unitId, out var v) ? v : Array.Empty<SkillBundle>());

        public Task<IReadOnlyList<SkillBundle>> SetAsync(string unitId, IReadOnlyList<SkillBundleReference> references, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SkillBundle>> AddAsync(string unitId, SkillBundleReference reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SkillBundle>> RemoveAsync(string unitId, string packageName, string skillName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string unitId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAgentSkillBundleStore : IAgentSkillBundleStore
    {
        private readonly Dictionary<string, IReadOnlyList<SkillBundle>> _byId = new(StringComparer.Ordinal);

        public void Set(string id, IReadOnlyList<SkillBundle> bundles) => _byId[id] = bundles;

        public Task<IReadOnlyList<SkillBundle>> GetAsync(string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SkillBundle>>(
                _byId.TryGetValue(agentId, out var v) ? v : Array.Empty<SkillBundle>());

        public Task<IReadOnlyList<SkillBundle>> SetAsync(string agentId, IReadOnlyList<SkillBundleReference> references, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SkillBundle>> AddAsync(string agentId, SkillBundleReference reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SkillBundle>> RemoveAsync(string agentId, string packageName, string skillName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
