// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Directory;
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
                new(new Address("agent", "contract-list"),
                    "actor-contract-list",
                    "Contract List",
                    "An agent for contract tests",
                    "backend",
                    DateTimeOffset.UtcNow),
            });

        var response = await _client.GetAsync("/api/v1/agents", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/agents", "get", "200", body);
    }

    [Fact]
    public async Task CreateAgent_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnitEntry("contract-unit", "actor-unit-contract");
        ArrangeAgentActorProxy();

        var request = new CreateAgentRequest(
            "contract-create",
            "Contract Create",
            "An agent for contract tests",
            Role: "backend",
            UnitIds: new[] { "contract-unit" });

        var response = await _client.PostAsJsonAsync("/api/v1/agents", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/agents", "post", "201", body);
    }

    [Fact]
    public async Task GetAgent_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "contract-ghost-agent"),
                Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync("/api/v1/agents/contract-ghost-agent", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/agents/{id}", "get", "404", body, "application/problem+json");
    }

    [Fact]
    public async Task DeployPersistentAgent_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "contract-ghost-deploy"),
                Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/agents/contract-ghost-deploy/deploy",
            new DeployPersistentAgentRequest(),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/agents/{id}/deploy", "post", "404", body, "application/problem+json");
    }

    [Fact]
    public async Task UndeployPersistentAgent_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "contract-undeploy"),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("agent", "contract-undeploy"),
                "actor-contract-undeploy",
                "Contract Undeploy",
                "",
                null,
                DateTimeOffset.UtcNow));

        // Undeploy is idempotent — when the agent has never been deployed,
        // the endpoint returns the canonical empty deployment shape so the
        // response carries every required field on the wire.
        var response = await _client.PostAsync(
            "/api/v1/agents/contract-undeploy/undeploy", content: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/agents/{id}/undeploy", "post", "200", body);
    }

    private void ArrangeUnitEntry(string unitId, string actorId)
    {
        var entry = new DirectoryEntry(
            new Address("unit", unitId),
            actorId,
            unitId,
            $"unit {unitId}",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == unitId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IUnitActor>();
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == actorId),
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