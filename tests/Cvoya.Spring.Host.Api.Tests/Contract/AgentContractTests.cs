// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Semantic contract tests for the <c>/api/v1/agents</c> surface (#1248 / C1.3).
/// Companion to the existing behavioural <c>AgentEndpointsTests</c> and
/// <c>PersistentAgentEndpointsTests</c> — those check what the endpoint does;
/// these check that response bodies match the committed openapi.json.
/// </summary>
public class AgentContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid Agent_AgentAutonomous_Id = new("00000001-0000-0000-0000-000000000000");
    private static readonly Guid Agent_AgentEphemeral_Id = new("00000002-0000-0000-0000-000000000000");
    private static readonly Guid Agent_AgentPassive_Id = new("00000003-0000-0000-0000-000000000000");
    private static readonly Guid Agent_AgentPersistent_Id = new("00000004-0000-0000-0000-000000000000");
    private static readonly Guid Agent_AgentProactive_Id = new("00000005-0000-0000-0000-000000000000");
    private static readonly Guid Agent_ContractHostingInitiative_Id = new("00000006-0000-0000-0000-000000000000");
    private static readonly Guid Agent_ContractList_Id = new("00000007-0000-0000-0000-000000000000");
    private static readonly Guid Agent_ContractUndeploy_Id = new("00000008-0000-0000-0000-000000000000");
    private static readonly Guid ActorAutonomous_Id = new("00000009-0000-0000-0000-000000000000");
    private static readonly Guid ActorContractList_Id = new("0000000a-0000-0000-0000-000000000000");
    private static readonly Guid ActorContractUndeploy_Id = new("0000000b-0000-0000-0000-000000000000");
    private static readonly Guid ActorEphemeral_Id = new("0000000c-0000-0000-0000-000000000000");
    private static readonly Guid ActorHostingInitiative_Id = new("0000000d-0000-0000-0000-000000000000");
    private static readonly Guid ActorPassive_Id = new("0000000e-0000-0000-0000-000000000000");
    private static readonly Guid ActorPersistent_Id = new("0000000f-0000-0000-0000-000000000000");
    private static readonly Guid ActorProactive_Id = new("00000010-0000-0000-0000-000000000000");
    private static readonly Guid ActorUnitContract_Id = new("00000011-0000-0000-0000-000000000000");

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AgentContractTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListAgents_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("agent", Agent_ContractList_Id),
                    ActorContractList_Id,
                    "Contract List",
                    "An agent for contract tests",
                    "backend",
                    DateTimeOffset.UtcNow),
            });

        var response = await _client.GetAsync("/api/v1/tenant/agents", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/agents", "get", "200", body);
    }

    [Fact]
    public async Task CreateAgent_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnitEntry("contract-unit", ActorUnitContract_Id);
        ArrangeAgentActorProxy();

        // Post-#1629 the agent's `name` is its Guid identity; the
        // human-readable label travels in DisplayName.
        var request = new CreateAgentRequest(
            Guid.NewGuid().ToString("N"),
            "Contract Create",
            "An agent for contract tests",
            Role: "backend",
            UnitIds: new[] { ActorUnitContract_Id.ToString("N") });

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/agents", "post", "201", body);
    }

    [Fact]
    public async Task GetAgent_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var ghostId = TestSlugIds.For("contract-ghost-agent");
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == ghostId),
                Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{ghostId:N}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/agents/{id}", "get", "404", body, "application/problem+json");
    }

    [Fact]
    public async Task DeployPersistentAgent_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var ghostId = TestSlugIds.For("contract-ghost-deploy");
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == ghostId),
                Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/agents/{ghostId:N}/deploy",
            new DeployPersistentAgentRequest(),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/agents/{id}/deploy", "post", "404", body, "application/problem+json");
    }

    [Fact]
    public async Task UndeployPersistentAgent_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == Agent_ContractUndeploy_Id),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("agent", Agent_ContractUndeploy_Id),
                ActorContractUndeploy_Id,
                "Contract Undeploy",
                "",
                null,
                DateTimeOffset.UtcNow));

        // Undeploy is idempotent — when the agent has never been deployed,
        // the endpoint returns the canonical empty deployment shape so the
        // response carries every required field on the wire.
        var response = await _client.PostAsync(
            $"/api/v1/tenant/agents/{Agent_ContractUndeploy_Id:N}/undeploy", content: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/agents/{id}/undeploy", "post", "200", body);
    }

    [Fact]
    public async Task ListAgents_IncludesHostingModeAndInitiativeLevel_MatchesContract()
    {
        // #572 / #573: the list endpoint must carry hostingMode and
        // initiativeLevel on every entry. Both are nullable — an agent
        // with no execution block carries null for hostingMode; an
        // agent with no policy carries "passive" for initiativeLevel
        // (the store default). The contract test pins the shape so a
        // field removal causes a compile error on the server side and a
        // validation failure here.
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("agent", Agent_ContractHostingInitiative_Id),
                    ActorHostingInitiative_Id,
                    "Hosting + Initiative",
                    "Contract test for new fields",
                    null,
                    DateTimeOffset.UtcNow),
            });

        var response = await _client.GetAsync("/api/v1/tenant/agents", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/agents", "get", "200", body);

        // Parse the response body and pin the new fields.
        using var doc = JsonDocument.Parse(body);
        var agent = doc.RootElement[0];

        // hostingMode is nullable — no execution block → null.
        agent.TryGetProperty("hostingMode", out var hostingMode).ShouldBeTrue(
            "AgentResponse must include 'hostingMode' (nullable string)");
        // The value may be null (no execution block) or a valid hosting key.
        if (hostingMode.ValueKind != JsonValueKind.Null)
        {
            hostingMode.GetString().ShouldBeOneOf("ephemeral", "persistent");
        }

        // initiativeLevel is nullable — default policy → "passive".
        agent.TryGetProperty("initiativeLevel", out var initiativeLevel).ShouldBeTrue(
            "AgentResponse must include 'initiativeLevel' (nullable string)");
        if (initiativeLevel.ValueKind != JsonValueKind.Null)
        {
            initiativeLevel.GetString().ShouldBeOneOf("passive", "attentive", "proactive", "autonomous");
        }
    }

    // --- #1402: server-side filter tests ---

    [Theory]
    [InlineData("ephemeral")]
    [InlineData("persistent")]
    public async Task ListAgents_HostingFilter_ReturnsMatchingAgentsOnly(string hostingMode)
    {
        // Arrange two agents: one ephemeral, one persistent.
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("agent", Agent_AgentEphemeral_Id),
                    ActorEphemeral_Id,
                    "Ephemeral Agent",
                    "",
                    null,
                    DateTimeOffset.UtcNow),
                new(new Address("agent", Agent_AgentPersistent_Id),
                    ActorPersistent_Id,
                    "Persistent Agent",
                    "",
                    null,
                    DateTimeOffset.UtcNow),
            });

        // Stub: ephemeral agent has hosting=ephemeral, persistent has hosting=persistent.
        _factory.AgentExecutionStore
            .GetAsync(
                Arg.Is<string>(id => id == Agent_AgentEphemeral_Id.ToString("N")),
                Arg.Any<CancellationToken>())
            .Returns(new AgentExecutionShape(Hosting: "ephemeral"));
        _factory.AgentExecutionStore
            .GetAsync(
                Arg.Is<string>(id => id == Agent_AgentPersistent_Id.ToString("N")),
                Arg.Any<CancellationToken>())
            .Returns(new AgentExecutionShape(Hosting: "persistent"));

        var response = await _client.GetAsync($"/api/v1/tenant/agents?hosting={hostingMode}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/agents", "get", "200", body);

        using var doc = JsonDocument.Parse(body);
        var agents = doc.RootElement.EnumerateArray().ToList();

        // All returned agents must have the requested hosting mode.
        agents.ShouldNotBeEmpty($"Expected at least one {hostingMode} agent");
        foreach (var agent in agents)
        {
            if (agent.TryGetProperty("hostingMode", out var hm) && hm.ValueKind != JsonValueKind.Null)
            {
                hm.GetString().ShouldBe(hostingMode);
            }
        }
    }

    [Fact]
    public async Task ListAgents_InitiativeFilter_MultiValue_ReturnsMatchingAgentsOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("agent", Agent_AgentPassive_Id),
                    ActorPassive_Id,
                    "Passive Agent",
                    "",
                    null,
                    DateTimeOffset.UtcNow),
                new(new Address("agent", Agent_AgentProactive_Id),
                    ActorProactive_Id,
                    "Proactive Agent",
                    "",
                    null,
                    DateTimeOffset.UtcNow),
                new(new Address("agent", Agent_AgentAutonomous_Id),
                    ActorAutonomous_Id,
                    "Autonomous Agent",
                    "",
                    null,
                    DateTimeOffset.UtcNow),
            });

        // Stub initiative levels via the engine.
        _factory.InitiativeEngine
            .GetCurrentLevelAsync(
                Arg.Is<string>(id => id == Agent_AgentPassive_Id.ToString("N")),
                Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Core.Initiative.InitiativeLevel.Passive);
        _factory.InitiativeEngine
            .GetCurrentLevelAsync(
                Arg.Is<string>(id => id == Agent_AgentProactive_Id.ToString("N")),
                Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Core.Initiative.InitiativeLevel.Proactive);
        _factory.InitiativeEngine
            .GetCurrentLevelAsync(
                Arg.Is<string>(id => id == Agent_AgentAutonomous_Id.ToString("N")),
                Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Core.Initiative.InitiativeLevel.Autonomous);

        // Request proactive + autonomous — passive should be excluded.
        var response = await _client.GetAsync(
            "/api/v1/tenant/agents?initiative=proactive&initiative=autonomous", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/agents", "get", "200", body);

        using var doc = JsonDocument.Parse(body);
        var agents = doc.RootElement.EnumerateArray().ToList();

        // Only proactive and autonomous agents should be returned.
        agents.Count.ShouldBe(2);
        var levels = agents
            .Where(a => a.TryGetProperty("initiativeLevel", out var il) && il.ValueKind != JsonValueKind.Null)
            .Select(a => a.GetProperty("initiativeLevel").GetString())
            .ToList();
        levels.ShouldContain("proactive");
        levels.ShouldContain("autonomous");
        levels.ShouldNotContain("passive");
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
                Arg.Is<global::Dapr.Actors.ActorId>(a => a.GetId() == actorId.ToString("N")),
                Arg.Any<string>())
            .Returns(proxy);
    }

    private void ArrangeAgentActorProxy()
    {
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(Substitute.For<IAgentActor>());
    }
}