// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <c>GET /api/v1/tenant/tree</c> (SVR-tenant-tree,
/// umbrella #815). Covers the synthesized root, unit-agent nesting,
/// multi-parent alias edges, and the <c>primaryParentId</c> flag.
/// </summary>
public class TenantTreeEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    // Tracks UUID actorIds assigned by ArrangeDirectoryEntries so that
    // UpsertMembershipAsync can seed membership rows with matching keys
    // (#1492: membership table is now keyed by UUID, not slug).
    private readonly Dictionary<string, Guid> _entryUuids = new(StringComparer.OrdinalIgnoreCase);

    public TenantTreeEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetTenantTree_EmptyTenant_ReturnsJustTheTenantRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryEntries();

        var response = await _client.GetAsync("/api/v1/tenant/tree", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TenantTreeResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Tree.Kind.ShouldBe("Tenant");
        body.Tree.Id.ShouldStartWith("tenant://");
        (body.Tree.Children ?? []).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTenantTree_SetsCacheControlHeader()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryEntries();

        var response = await _client.GetAsync("/api/v1/tenant/tree", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl!.Private.ShouldBeTrue();
        // Lowered from 15 → 1 in #1451 so post-mutation reads (e.g. the
        // wizard's create-unit flow) see fresh data on the very next
        // explorer render.
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetTenantTree_NestsAgentsUnderEveryParentUnit()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryEntries(
            units: [("engineering", "Engineering"), ("marketing", "Marketing")],
            agents: [("ada", "Ada Lovelace", "reviewer")]);

        // Ada is a multi-parent agent — belongs to both engineering (primary
        // by virtue of being the first insert) and marketing.
        await UpsertMembershipAsync("engineering", "ada");
        await Task.Delay(10, ct);
        await UpsertMembershipAsync("marketing", "ada");

        var body = await FetchTreeAsync(ct);
        var tenant = body!.Tree;
        tenant.Children!.Count.ShouldBe(2);

        var engId = _entryUuids["unit:engineering"].ToString("N");
        var marketingId = _entryUuids["unit:marketing"].ToString("N");
        var adaId = _entryUuids["agent:ada"].ToString("N");

        var engineering = tenant.Children!.Single(u => u.Id == engId);
        var marketing = tenant.Children!.Single(u => u.Id == marketingId);

        engineering.Children!.Single(a => a.Id == adaId).PrimaryParentId.ShouldBe(engId);
        marketing.Children!.Single(a => a.Id == adaId).PrimaryParentId.ShouldBe(engId);
    }

    [Fact]
    public async Task GetTenantTree_OmitsDisabledMemberships()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryEntries(
            units: [("engineering", "Engineering")],
            agents: [("ada", "Ada", null), ("hopper", "Grace", null)]);

        await UpsertMembershipAsync("engineering", "ada");
        await UpsertMembershipAsync("engineering", "hopper", enabled: false);

        var body = await FetchTreeAsync(ct);
        var engId = _entryUuids["unit:engineering"].ToString("N");
        var adaId = _entryUuids["agent:ada"].ToString("N");
        var engineering = body!.Tree.Children!.Single(u => u.Id == engId);
        engineering.Children!.Select(a => a.Id).ShouldBe([adaId]);
    }

    [Fact]
    public async Task GetTenantTree_AgentWithNoDirectoryEntry_IsOmitted()
    {
        // Transient state during registration: the membership row lands
        // before the directory entry. The endpoint must skip rather than
        // emit a half-formed node.
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryEntries(units: [("engineering", "Engineering")]);

        await UpsertMembershipAsync("engineering", "ghost-agent");

        var body = await FetchTreeAsync(ct);
        var engId = _entryUuids["unit:engineering"].ToString("N");
        var engineering = body!.Tree.Children!.Single(u => u.Id == engId);
        (engineering.Children ?? []).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTenantTree_EmitsLifecycleStatusFromActor_NotHardcodedRunning()
    {
        // #1032: the endpoint previously pinned every unit to "running"
        // regardless of actor state, which showed a green "Running" badge
        // on Draft units. The wire status must reflect what the actor
        // persisted — mapped to the lowercase vocabulary the portal
        // validator speaks.
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryEntries(
            units:
            [
                ("draft-unit", "Draft Unit"),
                ("running-unit", "Running Unit"),
                ("error-unit", "Error Unit"),
            ]);

        // ArrangeDirectoryEntries now assigns UUID actorIds; retrieve them
        // for wiring the actor-proxy stubs.
        ArrangeLifecycleStatus(_entryUuids["unit:draft-unit"].ToString("N"), LifecycleStatus.Draft);
        ArrangeLifecycleStatus(_entryUuids["unit:running-unit"].ToString("N"), LifecycleStatus.Running);
        ArrangeLifecycleStatus(_entryUuids["unit:error-unit"].ToString("N"), LifecycleStatus.Error);

        var body = await FetchTreeAsync(ct);
        var tenant = body!.Tree;

        var draftId = _entryUuids["unit:draft-unit"].ToString("N");
        var runningId = _entryUuids["unit:running-unit"].ToString("N");
        var errorId = _entryUuids["unit:error-unit"].ToString("N");

        tenant.Children!.Single(u => u.Id == draftId).Status.ShouldBe("draft");
        tenant.Children!.Single(u => u.Id == runningId).Status.ShouldBe("running");
        tenant.Children!.Single(u => u.Id == errorId).Status.ShouldBe("error");
    }

    [Fact]
    public async Task GetTenantTree_UnreachableUnitActor_FallsBackToDraft()
    {
        // A unit's actor can be transiently unreachable (fresh
        // registration, Dapr sidecar restart). The endpoint must still
        // render the tree — Draft is the safest fallback (matches the
        // policy shared with DashboardEndpoints).
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryEntries(units: [("flaky", "Flaky Unit")]);
        ArrangeLifecycleStatusThrows(_entryUuids["unit:flaky"].ToString("N"));

        var body = await FetchTreeAsync(ct);
        var flakyId = _entryUuids["unit:flaky"].ToString("N");
        body!.Tree.Children!.Single(u => u.Id == flakyId).Status.ShouldBe("draft");
    }

    [Fact]
    public async Task GetTenantTree_SurfacesAgentRoleFromDirectoryEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryEntries(
            units: [("engineering", "Engineering")],
            agents: [("ada", "Ada Lovelace", "reviewer")]);
        await UpsertMembershipAsync("engineering", "ada");

        var body = await FetchTreeAsync(ct);
        var engId = _entryUuids["unit:engineering"].ToString("N");
        var adaId = _entryUuids["agent:ada"].ToString("N");
        var engineering = body!.Tree.Children!.Single(u => u.Id == engId);
        var ada = engineering.Children!.Single(a => a.Id == adaId);
        ada.Role.ShouldBe("reviewer");
        ada.Name.ShouldBe("Ada Lovelace");
    }

    [Fact]
    public async Task GetTenantTree_EmitsHumanNodesUnderEveryUnitTheyBelongTo()
    {
        // #2466: human team-role members surface as a third child kind
        // on every unit. The same human registered on two units appears
        // twice — one node per `(unit, human)` membership row. Each
        // node carries the canonical `human://<guid>` id so the
        // Explorer's `human:` selection handler routes the click to
        // `/humans/<guid>`.
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryEntries(
            units: [("engineering", "Engineering"), ("research", "Research")]);

        var operatorHumanId = await SeedHumanAsync("operator", "Operator");
        await UpsertHumanMembershipAsync("engineering", operatorHumanId, roles: ["lead"]);
        await UpsertHumanMembershipAsync("research", operatorHumanId, roles: ["reviewer"]);

        var body = await FetchTreeAsync(ct);
        var engId = _entryUuids["unit:engineering"].ToString("N");
        var researchId = _entryUuids["unit:research"].ToString("N");
        var engineering = body!.Tree.Children!.Single(u => u.Id == engId);
        var research = body.Tree.Children!.Single(u => u.Id == researchId);

        var humanGuid = operatorHumanId.ToString("N");
        var expectedId = $"human://{humanGuid}";

        var engHuman = engineering.Children!.Single(c => c.Id == expectedId);
        engHuman.Kind.ShouldBe("Human");
        engHuman.Name.ShouldBe("Operator");
        engHuman.Status.ShouldBe("running");
        engHuman.DefinitionId.ShouldBe(operatorHumanId);

        var researchHuman = research.Children!.Single(c => c.Id == expectedId);
        researchHuman.Kind.ShouldBe("Human");
        researchHuman.Name.ShouldBe("Operator");
    }

    [Fact]
    public async Task GetTenantTree_OmitsHumanRowsWithNoBackingEntity()
    {
        // Race window during onboarding: a membership row can exist
        // before the Humans table has been seeded. The endpoint must
        // skip rather than emit a half-formed node with a Guid name.
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryEntries(units: [("engineering", "Engineering")]);

        var ghostHumanId = Guid.NewGuid();
        await UpsertHumanMembershipAsync("engineering", ghostHumanId);

        var body = await FetchTreeAsync(ct);
        var engId = _entryUuids["unit:engineering"].ToString("N");
        var engineering = body!.Tree.Children!.Single(u => u.Id == engId);
        (engineering.Children ?? []).ShouldNotContain(c => c.Kind == "Human");
    }

    private async Task<TenantTreeResponse?> FetchTreeAsync(CancellationToken ct)
    {
        var response = await _client.GetAsync("/api/v1/tenant/tree", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<TenantTreeResponse>(JsonOptions, ct);
    }

    private void ClearMemberships()
    {
        _entryUuids.Clear();
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DirectoryEntry>());

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Cvoya.Spring.Dapr.Data.SpringDbContext>();
        ctx.UnitMemberships.RemoveRange(ctx.UnitMemberships.ToList());
        ctx.UnitMembershipsHumans.RemoveRange(ctx.UnitMembershipsHumans.ToList());
        ctx.Humans.RemoveRange(ctx.Humans.ToList());
        ctx.SaveChanges();
    }

    private void ArrangeDirectoryEntries(
        (string Path, string DisplayName)[]? units = null,
        (string Path, string DisplayName, string? Role)[]? agents = null)
    {
        var list = new List<DirectoryEntry>();
        foreach (var (path, displayName) in units ?? Array.Empty<(string, string)>())
        {
            // #1492: use a deterministic UUID actorId so UpsertMembershipAsync
            // can seed membership rows whose UnitId matches the entry.ActorId.
            var uuid = Guid.NewGuid();
            _entryUuids[$"unit:{path}"] = uuid;
            list.Add(new DirectoryEntry(
                Address: new Address("unit", uuid),
                ActorId: uuid,
                DisplayName: displayName,
                Description: string.Empty,
                Role: null,
                RegisteredAt: DateTimeOffset.UtcNow));
        }
        foreach (var (path, displayName, role) in agents ?? Array.Empty<(string, string, string?)>())
        {
            var uuid = Guid.NewGuid();
            _entryUuids[$"agent:{path}"] = uuid;
            list.Add(new DirectoryEntry(
                Address: new Address("agent", uuid),
                ActorId: uuid,
                DisplayName: displayName,
                Description: string.Empty,
                Role: role,
                RegisteredAt: DateTimeOffset.UtcNow));
        }

        _factory.DirectoryService
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(list);
    }

    private void ArrangeLifecycleStatus(string actorId, LifecycleStatus status)
    {
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(status);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == actorId),
                Arg.Any<string>())
            .Returns(proxy);
    }

    private void ArrangeLifecycleStatusThrows(string actorId)
    {
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns<Task<LifecycleStatus>>(_ => throw new InvalidOperationException("actor unreachable"));
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == actorId),
                Arg.Any<string>())
            .Returns(proxy);
    }

    /// <summary>
    /// Seeds a membership row in the DB using the UUID actorIds that
    /// <see cref="ArrangeDirectoryEntries"/> assigned. Falls back to a new
    /// <see cref="Guid"/> when a slug has no corresponding directory entry
    /// (intentional ghost-agent / ghost-unit scenarios).
    /// </summary>
    private async Task UpsertMembershipAsync(string unitPath, string agentPath, bool enabled = true)
    {
        var unitUuid = _entryUuids.TryGetValue($"unit:{unitPath}", out var uid) ? uid : Guid.NewGuid();
        var agentUuid = _entryUuids.TryGetValue($"agent:{agentPath}", out var aid) ? aid : Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        await repo.UpsertAsync(
            new UnitMembership(unitUuid, agentUuid, Enabled: enabled),
            CancellationToken.None);
    }

    /// <summary>
    /// Seeds a Humans row in the DB with the given username + display
    /// name. Returns the generated stable id so the caller can wire
    /// membership rows that point back to this human.
    /// </summary>
    private async Task<Guid> SeedHumanAsync(string username, string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Cvoya.Spring.Dapr.Data.SpringDbContext>();
        var row = new Cvoya.Spring.Dapr.Data.Entities.HumanEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Cvoya.Spring.Core.Tenancy.OssTenantIds.Default,
            Username = username,
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Humans.Add(row);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        return row.Id;
    }

    /// <summary>
    /// Seeds a unit_memberships_humans row via the production store so
    /// the tenant-tree endpoint sees the row through the same code path
    /// the CLI / portal write surface uses.
    /// </summary>
    private async Task UpsertHumanMembershipAsync(
        string unitPath,
        Guid humanId,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<string>? expertise = null,
        IReadOnlyList<string>? notifications = null)
    {
        var unitUuid = _entryUuids.TryGetValue($"unit:{unitPath}", out var uid) ? uid : Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IUnitHumanMembershipStore>();
        await store.UpsertAsync(
            unitUuid,
            humanId,
            roles ?? Array.Empty<string>(),
            expertise ?? Array.Empty<string>(),
            notifications ?? Array.Empty<string>(),
            TestContext.Current.CancellationToken);
    }
}
