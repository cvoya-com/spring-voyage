// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

public class AgentEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    // Server serialises enums as strings (Program.cs#134); tests must match.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // Stable UUID for the "engineering" unit actor (#1492: endpoints now
    // require Guid-parseable ActorIds for membership lookups).
    private static readonly Guid UnitEngineeringUuid = new("ee1ee111-0000-0000-0000-000000000001");

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AgentEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListAgents_ReturnsAgentsFromDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", agentId), agentId, "Test Agent", "A test agent", "backend", DateTimeOffset.UtcNow),
            new(new Address("unit", unitId), unitId, "Test Unit", "A test unit", null, DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var response = await _client.GetAsync("/api/v1/tenant/agents", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents!.Count().ShouldBe(1);
        // Post-#1629 the AgentResponse Name and DisplayName both project the
        // entry's DisplayName (slug-form preserved for legacy compat); Id
        // is the agent's Guid hex.
        agents![0].Id.ShouldBe(agentId);
        agents[0].Name.ShouldBe("Test Agent");
        agents[0].DisplayName.ShouldBe("Test Agent");
        agents[0].Role.ShouldBe("backend");
    }

    [Fact]
    public async Task CreateAgent_RegistersAndReturnsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        // Clear any residual membership rows from previous tests that share
        // the IClassFixture in-memory DB.
        ClearMemberships();
        ArrangeUnitEntry("engineering", UnitEngineeringUuid);
        ArrangeAgentActorProxy();

        var request = new CreateAgentRequest(
            "New Agent", "A brand new agent", "frontend",
            UnitIds: new[] { UnitEngineeringUuid });

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldContain("/api/v1/tenant/agents/");

        await _factory.DirectoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Address.Scheme == "agent" &&
                e.DisplayName == "New Agent"),
            Arg.Any<CancellationToken>());

        // Verify the membership row was written. Agent UUID is assigned by
        // the endpoint (Guid.NewGuid()), so query by unit UUID and check any
        // row exists for the engineering unit (#1492).
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        var members = await repo.ListByUnitAsync(UnitEngineeringUuid, ct);
        members.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task CreateAgent_EmptyUnitIds_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();

        var request = new CreateAgentRequest(
            "Orphan", "A would-be orphan", "frontend",
            UnitIds: Array.Empty<Guid>());

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.DirectoryService.DidNotReceive().RegisterAsync(
            Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAgent_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var request = new CreateAgentRequest(
            "Lost", "Unit does not exist", "frontend",
            UnitIds: new[] { Guid.NewGuid() });

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await _factory.DirectoryService.DidNotReceive().RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Scheme == "agent"),
            Arg.Any<CancellationToken>());
    }

    private void ArrangeUnitEntry(string displayName, Guid actorId)
    {
        var entry = new DirectoryEntry(
            new Address("unit", actorId),
            actorId,
            displayName,
            $"unit {displayName}",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == actorId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IUnitActor>();
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == actorId.ToString("N")),
                Arg.Any<string>())
            .Returns(proxy);
    }

    private void ArrangeAgentActorProxy()
    {
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(Substitute.For<IAgentActor>());
    }

    private void ClearMemberships()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Cvoya.Spring.Dapr.Data.SpringDbContext>();
        ctx.UnitMemberships.RemoveRange(ctx.UnitMemberships.ToList());
        ctx.SaveChanges();
    }

    // -------------------------------------------------------------------
    // #1649: server-side search filters on GET /api/v1/tenant/agents.
    // The CLI's `agent show <name>` resolver (PR #1650) used to list
    // every agent and filter client-side. With ?display_name= and
    // ?unit_id= the resolver collapses to one round-trip per call.
    //
    // Each test seeds DirectoryService.ListAllAsync with three agents +
    // one unit, optionally seeds membership rows (real EF repo via the
    // in-memory DB), then asserts the wire-shape returned by the endpoint.
    // -------------------------------------------------------------------

    [Fact]
    public async Task ListAgents_DisplayNameFilter_NoMatch_ReturnsEmptyArray()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        SeedThreeAgentsAndOneUnit();

        var response = await _client.GetAsync(
            "/api/v1/tenant/agents?display_name=ghost", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAgents_DisplayNameFilter_OneMatch_ReturnsSingleAgent()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        SeedThreeAgentsAndOneUnit();

        var response = await _client.GetAsync(
            "/api/v1/tenant/agents?display_name=Alice", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.Count.ShouldBe(1);
        agents[0].DisplayName.ShouldBe("Alice");
    }

    [Fact]
    public async Task ListAgents_DisplayNameFilter_CaseInsensitive_ReturnsMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        SeedThreeAgentsAndOneUnit();

        var response = await _client.GetAsync(
            "/api/v1/tenant/agents?display_name=ALICE", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.Count.ShouldBe(1);
        agents[0].DisplayName.ShouldBe("Alice");
    }

    [Fact]
    public async Task ListAgents_DisplayNameFilter_MultipleMatches_ReturnsAll()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        SeedAgentsWithDuplicateDisplayName();

        var response = await _client.GetAsync(
            "/api/v1/tenant/agents?display_name=Alice", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.Count.ShouldBe(2);
        agents.ShouldAllBe(a => a.DisplayName == "Alice");
    }

    [Fact]
    public async Task ListAgents_UnitIdFilter_NarrowsToMembershipMembers()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        var (alice, bob, _) = SeedThreeAgentsAndOneUnit();

        // Only Alice is a member of the engineering unit.
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
            await repo.UpsertAsync(
                new Cvoya.Spring.Core.Units.UnitMembership(
                    UnitId: UnitEngineeringUuid,
                    AgentId: alice,
                    Enabled: true),
                ct);
        }

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents?unit_id={UnitEngineeringUuid:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.Count.ShouldBe(1);
        agents[0].Id.ShouldBe(alice);
    }

    [Fact]
    public async Task ListAgents_DisplayNameAndUnitIdFilters_Compose()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        var (alice, bob, _) = SeedThreeAgentsAndOneUnit();

        // Both Alice and Bob are in engineering, but display_name=Alice
        // narrows to one.
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
            await repo.UpsertAsync(
                new Cvoya.Spring.Core.Units.UnitMembership(
                    UnitId: UnitEngineeringUuid,
                    AgentId: alice,
                    Enabled: true),
                ct);
            await repo.UpsertAsync(
                new Cvoya.Spring.Core.Units.UnitMembership(
                    UnitId: UnitEngineeringUuid,
                    AgentId: bob,
                    Enabled: true),
                ct);
        }

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents?display_name=Alice&unit_id={UnitEngineeringUuid:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.Count.ShouldBe(1);
        agents[0].Id.ShouldBe(alice);
        agents[0].DisplayName.ShouldBe("Alice");
    }

    [Fact]
    public async Task ListAgents_UnitIdFilter_NotMember_ReturnsEmptyArray()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        SeedThreeAgentsAndOneUnit();

        // No memberships seeded ⇒ the engineering unit has zero members.
        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents?unit_id={UnitEngineeringUuid:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAgents_MalformedUnitId_ReturnsEmptyArray()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        SeedThreeAgentsAndOneUnit();

        var response = await _client.GetAsync(
            "/api/v1/tenant/agents?unit_id=not-a-guid", ct);

        // Malformed unit_id is treated as "no match" rather than 400 — the
        // empty result is the canonical "no matches" wire shape and the CLI
        // never sends a malformed unit_id (it parses through GuidFormatter
        // before dispatching).
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.ShouldBeEmpty();
    }

    /// <summary>
    /// Seeds the directory mock with three agents (Alice / Bob / Carol)
    /// and the engineering unit. Returns the agents' Guids so individual
    /// tests can wire memberships through the real EF repo.
    /// </summary>
    private (Guid alice, Guid bob, Guid carol) SeedThreeAgentsAndOneUnit()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var carol = Guid.NewGuid();

        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", alice), alice, "Alice", "alice", null, DateTimeOffset.UtcNow),
            new(new Address("agent", bob), bob, "Bob", "bob", null, DateTimeOffset.UtcNow),
            new(new Address("agent", carol), carol, "Carol", "carol", null, DateTimeOffset.UtcNow),
            new(new Address("unit", UnitEngineeringUuid), UnitEngineeringUuid, "engineering", "eng", null, DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(entries);

        return (alice, bob, carol);
    }

    /// <summary>
    /// Seeds two agents that both carry the display_name "Alice" — used to
    /// verify the n-match path returns the full candidate list.
    /// </summary>
    private void SeedAgentsWithDuplicateDisplayName()
    {
        var aliceOne = Guid.NewGuid();
        var aliceTwo = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", aliceOne), aliceOne, "Alice", "alice", null, DateTimeOffset.UtcNow),
            new(new Address("agent", aliceTwo), aliceTwo, "Alice", "alice", null, DateTimeOffset.UtcNow),
            new(new Address("agent", bob), bob, "Bob", "bob", null, DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(entries);
    }
}