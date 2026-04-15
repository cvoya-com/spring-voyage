// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Agents;
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
/// Integration tests for the unit-scoped agent endpoints
/// (<c>GET / POST / DELETE /api/v1/units/{id}/agents[/{agentId}]</c>) and
/// the <c>PATCH /api/v1/agents/{id}</c> metadata route. In C2b-1 the
/// assign/unassign paths now read/write the <c>IUnitMembershipRepository</c>
/// instead of enforcing a 1:N parent-unit invariant.
/// </summary>
public class UnitAgentsEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string UnitName = "engineering";
    private const string UnitActorId = "actor-engineering";

    // Server uses JsonStringEnumConverter (Program.cs#134); tests must match.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitAgentsEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListUnitAgents_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync($"/api/v1/units/{UnitName}/agents", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListUnitAgents_ReturnsAgentMembersEnrichedWithMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        unitProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new Address[]
            {
                new("agent", "ada"),
                new("unit", "marketing"), // sub-unit member — must be filtered out
            });

        ArrangeAgent("ada", "actor-ada",
            new AgentMetadata(
                Model: "claude-opus",
                Specialty: "reviewer",
                Enabled: true,
                ExecutionMode: AgentExecutionMode.OnDemand));

        // Derived parent comes from the membership repository, not the actor
        // state — so arrange a membership row for this agent in this unit.
        await UpsertMembershipAsync(UnitName, "ada");

        var response = await _client.GetAsync($"/api/v1/units/{UnitName}/agents", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents!.Count.ShouldBe(1);
        agents[0].Name.ShouldBe("ada");
        agents[0].Model.ShouldBe("claude-opus");
        agents[0].Specialty.ShouldBe("reviewer");
        agents[0].Enabled.ShouldBeTrue();
        agents[0].ExecutionMode.ShouldBe(AgentExecutionMode.OnDemand);
        agents[0].ParentUnit.ShouldBe(UnitName);
    }

    [Fact]
    public async Task AssignUnitAgent_NewAgent_CreatesMembershipAndAddsMember()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        ArrangeAgent("ada", "actor-ada", new AgentMetadata());

        var response = await _client.PostAsync(
            $"/api/v1/units/{UnitName}/agents/ada", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var membership = await GetMembershipAsync(UnitName, "ada");
        membership.ShouldNotBeNull();
        membership!.Enabled.ShouldBeTrue();

        await unitProxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "ada"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignUnitAgent_SameAgentInMultipleUnits_BothMembershipsExist()
    {
        // C2b-1 removes the 1:N conflict check. An agent may belong to any
        // number of units, and each membership is stored independently.
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        ArrangeUnit();
        ArrangeUnit("marketing", "actor-marketing");
        ArrangeAgent("ada", "actor-ada", new AgentMetadata());

        (await _client.PostAsync($"/api/v1/units/{UnitName}/agents/ada", content: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.PostAsync("/api/v1/units/marketing/agents/ada", content: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await GetMembershipAsync(UnitName, "ada")).ShouldNotBeNull();
        (await GetMembershipAsync("marketing", "ada")).ShouldNotBeNull();
    }

    [Fact]
    public async Task AssignUnitAgent_AgentAlreadyBelongsToThisUnit_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        ArrangeAgent("ada", "actor-ada", new AgentMetadata());
        await UpsertMembershipAsync(UnitName, "ada");

        var response = await _client.PostAsync(
            $"/api/v1/units/{UnitName}/agents/ada", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Re-asserting membership is harmless and makes the endpoint
        // safe to retry.
        (await GetMembershipAsync(UnitName, "ada")).ShouldNotBeNull();
        await unitProxy.Received(1).AddMemberAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnassignUnitAgent_RemovesMembershipAndMember()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        var agentProxy = ArrangeAgent("ada", "actor-ada", new AgentMetadata());
        await UpsertMembershipAsync(UnitName, "ada");

        var response = await _client.DeleteAsync(
            $"/api/v1/units/{UnitName}/agents/ada", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetMembershipAsync(UnitName, "ada")).ShouldBeNull();

        await unitProxy.Received(1).RemoveMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "ada"),
            Arg.Any<CancellationToken>());
        // Cached pointer is cleared because this was the agent's only membership.
        await agentProxy.Received(1).ClearParentUnitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnassignUnitAgent_OtherMembershipSurvives()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        ArrangeUnit();
        ArrangeUnit("marketing", "actor-marketing");
        ArrangeAgent("ada", "actor-ada", new AgentMetadata());

        await UpsertMembershipAsync(UnitName, "ada");
        await UpsertMembershipAsync("marketing", "ada");

        var response = await _client.DeleteAsync(
            $"/api/v1/units/{UnitName}/agents/ada", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetMembershipAsync(UnitName, "ada")).ShouldBeNull();
        (await GetMembershipAsync("marketing", "ada")).ShouldNotBeNull();
    }

    [Fact]
    public async Task PatchAgent_PartialFields_CallsSetMetadataAndReturnsMerged()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var agentProxy = ArrangeAgent("ada", "actor-ada",
            new AgentMetadata(
                Model: "claude-opus",
                Specialty: "reviewer",
                Enabled: true,
                ExecutionMode: AgentExecutionMode.Auto));

        var patch = new UpdateAgentMetadataRequest(Enabled: false);
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/agents/ada")
        {
            Content = JsonContent.Create(patch, options: JsonOptions),
        };

        var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The endpoint forwards the partial patch verbatim; the actor handles
        // the "null means leave untouched" merge internally. Critically, the
        // endpoint must NOT pass ParentUnit — containment is only mutable via
        // the unit's assign / unassign routes.
        await agentProxy.Received(1).SetMetadataAsync(
            Arg.Is<AgentMetadata>(m =>
                m.Enabled == false &&
                m.Model == null &&
                m.Specialty == null &&
                m.ExecutionMode == null &&
                m.ParentUnit == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchAgent_UnknownAgent_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var patch = new UpdateAgentMetadataRequest(Model: "gpt-4");
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/agents/ghost")
        {
            Content = JsonContent.Create(patch, options: JsonOptions),
        };

        var response = await _client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private void ClearAllMocks()
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        // Each test gets a fresh scoped repository view via the DI container;
        // the underlying in-memory DB is per-factory but we clear rows here
        // so tests don't leak rows into each other.
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Cvoya.Spring.Dapr.Data.SpringDbContext>();
        ctx.UnitMemberships.RemoveRange(ctx.UnitMemberships.ToList());
        ctx.SaveChanges();
    }

    private IUnitActor ArrangeUnit(string name = UnitName, string actorId = UnitActorId)
    {
        var entry = new DirectoryEntry(
            new Address("unit", name),
            actorId,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == name),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IUnitActor>();
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<ActorId>(a => a.GetId() == actorId),
                Arg.Any<string>())
            .Returns(proxy);
        return proxy;
    }

    private IAgentActor ArrangeAgent(string agentId, string actorId, AgentMetadata metadata)
    {
        var entry = new DirectoryEntry(
            new Address("agent", agentId),
            actorId,
            agentId,
            $"Agent {agentId}",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == agentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IAgentActor>();
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>()).Returns(metadata);
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(Arg.Is<ActorId>(a => a.GetId() == actorId),
                Arg.Any<string>())
            .Returns(proxy);
        return proxy;
    }

    private async Task UpsertMembershipAsync(string unitId, string agentAddress)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        await repo.UpsertAsync(
            new UnitMembership(UnitId: unitId, AgentAddress: agentAddress, Enabled: true),
            CancellationToken.None);
    }

    private async Task<UnitMembership?> GetMembershipAsync(string unitId, string agentAddress)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        return await repo.GetAsync(unitId, agentAddress, CancellationToken.None);
    }
}