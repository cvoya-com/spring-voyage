// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Directory;
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

        var engineering = tenant.Children!.Single(u => u.Id == "engineering");
        var marketing = tenant.Children!.Single(u => u.Id == "marketing");

        engineering.Children!.Single(a => a.Id == "ada").PrimaryParentId.ShouldBe("engineering");
        marketing.Children!.Single(a => a.Id == "ada").PrimaryParentId.ShouldBe("engineering");
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
        var engineering = body!.Tree.Children!.Single(u => u.Id == "engineering");
        engineering.Children!.Select(a => a.Id).ShouldBe(["ada"]);
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
        var engineering = body!.Tree.Children!.Single(u => u.Id == "engineering");
        (engineering.Children ?? []).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTenantTree_EmitsUnitStatusFromActor_NotHardcodedRunning()
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
        ArrangeUnitStatus(_entryUuids["unit:draft-unit"].ToString(), UnitStatus.Draft);
        ArrangeUnitStatus(_entryUuids["unit:running-unit"].ToString(), UnitStatus.Running);
        ArrangeUnitStatus(_entryUuids["unit:error-unit"].ToString(), UnitStatus.Error);

        var body = await FetchTreeAsync(ct);
        var tenant = body!.Tree;

        tenant.Children!.Single(u => u.Id == "draft-unit").Status.ShouldBe("draft");
        tenant.Children!.Single(u => u.Id == "running-unit").Status.ShouldBe("running");
        tenant.Children!.Single(u => u.Id == "error-unit").Status.ShouldBe("error");
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
        ArrangeUnitStatusThrows(_entryUuids["unit:flaky"].ToString());

        var body = await FetchTreeAsync(ct);
        body!.Tree.Children!.Single(u => u.Id == "flaky").Status.ShouldBe("draft");
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
        var engineering = body!.Tree.Children!.Single(u => u.Id == "engineering");
        var ada = engineering.Children!.Single(a => a.Id == "ada");
        ada.Role.ShouldBe("reviewer");
        ada.Name.ShouldBe("Ada Lovelace");
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
                Address: new Address("unit", path),
                ActorId: uuid.ToString(),
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
                Address: new Address("agent", path),
                ActorId: uuid.ToString(),
                DisplayName: displayName,
                Description: string.Empty,
                Role: role,
                RegisteredAt: DateTimeOffset.UtcNow));
        }

        _factory.DirectoryService
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(list);
    }

    private void ArrangeUnitStatus(string actorId, UnitStatus status)
    {
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(status);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == actorId),
                Arg.Any<string>())
            .Returns(proxy);
    }

    private void ArrangeUnitStatusThrows(string actorId)
    {
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns<Task<UnitStatus>>(_ => throw new InvalidOperationException("actor unreachable"));
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
}