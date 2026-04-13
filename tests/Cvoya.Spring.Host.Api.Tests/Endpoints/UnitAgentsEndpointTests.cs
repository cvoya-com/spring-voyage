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
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the unit-scoped agent endpoints
/// (<c>GET / POST / DELETE /api/v1/units/{id}/agents[/{agentId}]</c>) and
/// the new <c>PATCH /api/v1/agents/{id}</c> metadata route.
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
            .Returns(new List<Address>
            {
                new("agent", "ada"),
                new("unit", "marketing"), // sub-unit member — must be filtered out
            });

        ArrangeAgent("ada", "actor-ada",
            new AgentMetadata(
                Model: "claude-opus",
                Specialty: "reviewer",
                Enabled: true,
                ExecutionMode: AgentExecutionMode.OnDemand,
                ParentUnit: UnitName));

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
    public async Task AssignUnitAgent_NewAgent_SetsParentAndAddsMember()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        var agentProxy = ArrangeAgent("ada", "actor-ada", new AgentMetadata());

        var response = await _client.PostAsync(
            $"/api/v1/units/{UnitName}/agents/ada", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await agentProxy.Received(1).SetMetadataAsync(
            Arg.Is<AgentMetadata>(m => m.ParentUnit == UnitName),
            Arg.Any<CancellationToken>());
        await unitProxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "ada"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignUnitAgent_AgentAlreadyBelongsToDifferentUnit_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        var agentProxy = ArrangeAgent("ada", "actor-ada",
            new AgentMetadata(ParentUnit: "marketing"));

        var response = await _client.PostAsync(
            $"/api/v1/units/{UnitName}/agents/ada", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Neither side of the assignment must run when the invariant would
        // be violated — the whole point of the 409 is to protect both
        // stores from drift.
        await agentProxy.DidNotReceive().SetMetadataAsync(
            Arg.Any<AgentMetadata>(), Arg.Any<CancellationToken>());
        await unitProxy.DidNotReceive().AddMemberAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignUnitAgent_AgentAlreadyBelongsToThisUnit_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        var agentProxy = ArrangeAgent("ada", "actor-ada",
            new AgentMetadata(ParentUnit: UnitName));

        var response = await _client.PostAsync(
            $"/api/v1/units/{UnitName}/agents/ada", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Re-asserting pointer + membership is harmless and makes the endpoint
        // safe to retry.
        await agentProxy.Received(1).SetMetadataAsync(
            Arg.Is<AgentMetadata>(m => m.ParentUnit == UnitName),
            Arg.Any<CancellationToken>());
        await unitProxy.Received(1).AddMemberAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnassignUnitAgent_RemovesMemberAndClearsParent()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        var agentProxy = ArrangeAgent("ada", "actor-ada",
            new AgentMetadata(ParentUnit: UnitName));

        var response = await _client.DeleteAsync(
            $"/api/v1/units/{UnitName}/agents/ada", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await unitProxy.Received(1).RemoveMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "ada"),
            Arg.Any<CancellationToken>());
        await agentProxy.Received(1).ClearParentUnitAsync(Arg.Any<CancellationToken>());
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
                ExecutionMode: AgentExecutionMode.Auto,
                ParentUnit: UnitName));

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
    }

    private IUnitActor ArrangeUnit()
    {
        var entry = new DirectoryEntry(
            new Address("unit", UnitName),
            UnitActorId,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IUnitActor>();
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<ActorId>(a => a.GetId() == UnitActorId),
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
}