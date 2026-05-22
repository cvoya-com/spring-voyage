// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Endpoint-level coverage for the persistent-agent lifecycle surface (#396).
/// ADR-0052 / Wave 3 (#2618): the API host delegates deploy / undeploy /
/// scale / deployment-status / logs to the execution host (<c>spring-worker</c>)
/// over <see cref="IPersistentAgentExecutionGateway"/>. These tests drive a
/// substitute gateway and assert the wire contract: 404 shape when the agent
/// does not exist, idempotent undeploy, canonical empty-deployment shape, and
/// the translation of a worker-side rejection into a 4xx.
/// </summary>
public class PersistentAgentEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid Agent_A_Id = new("00000001-1234-5678-9abc-000000000000");
    private static readonly Guid Agent_Idle_Id = new("00000002-1234-5678-9abc-000000000000");
    private static readonly Guid Actor1_Id = new("00000003-1234-5678-9abc-000000000000");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PersistentAgentEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static DirectoryEntry AgentEntry(Guid agentId) =>
        new(new Address("agent", agentId), Actor1_Id, "Agent", "", null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Deploy_WhenAgentNotInDirectory_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var ghostId = Guid.NewGuid();
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Id == ghostId), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/agents/{ghostId:N}/deploy", new DeployPersistentAgentRequest(), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Undeploy_IsIdempotentForAgentThatIsNotDeployed()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Id == Agent_Idle_Id), Arg.Any<CancellationToken>())
            .Returns(AgentEntry(Agent_Idle_Id));

        // The gateway's UndeployAsync is idempotent — the worker returns the
        // canonical "not running" state when nothing is tracked.
        _factory.PersistentAgentExecutionGateway
            .UndeployAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PersistentAgentDeploymentState.NotRunning(Actor1_Id.ToString("N")));

        var response = await _client.PostAsync($"/api/v1/tenant/agents/{Agent_Idle_Id:N}/undeploy", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<PersistentAgentDeploymentResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.Running.ShouldBeFalse();
        body.HealthStatus.ShouldBe("unknown");
        body.ContainerId.ShouldBeNull();
    }

    [Fact]
    public async Task Scale_WithReplicasAboveOne_Returns400WithMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Id == Agent_A_Id), Arg.Any<CancellationToken>())
            .Returns(AgentEntry(Agent_A_Id));

        // The execution host rejects replicas > 1; the gateway surfaces that
        // rejection as a SpringException, which the endpoint translates to 400.
        _factory.PersistentAgentExecutionGateway
            .ScaleAsync(Arg.Any<string>(), Arg.Is(2), Arg.Any<CancellationToken>())
            .Returns<PersistentAgentDeploymentState>(_ => throw new SpringException(
                "Horizontal scaling (replicas > 1) is not supported by the OSS core yet."));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/agents/{Agent_A_Id:N}/scale",
            new ScalePersistentAgentRequest(2),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetDeployment_WhenAgentExistsButNotDeployed_ReturnsEmptyShape()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Id == Agent_A_Id), Arg.Any<CancellationToken>())
            .Returns(AgentEntry(Agent_A_Id));

        // No tracked deployment — the worker returns the canonical
        // "not running" state.
        _factory.PersistentAgentExecutionGateway
            .GetDeploymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PersistentAgentDeploymentState.NotRunning(Actor1_Id.ToString("N")));

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{Agent_A_Id:N}/deployment", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<PersistentAgentDeploymentResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.AgentId.ShouldBe(Agent_A_Id.ToString("N"));
        body.Running.ShouldBeFalse();
        body.Replicas.ShouldBe(0);
    }

    [Fact]
    public async Task GetLogs_WhenAgentNotDeployed_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Id == Agent_A_Id), Arg.Any<CancellationToken>())
            .Returns(AgentEntry(Agent_A_Id));

        // The worker's logs route 404s when no deployment is tracked; the
        // gateway surfaces that as a SpringException and the endpoint
        // translates it into a 404 so the CLI shows a clear "no deployment"
        // message.
        _factory.PersistentAgentExecutionGateway
            .GetLogsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<PersistentAgentLogsState>(_ => throw new SpringException(
                "Persistent agent is not deployed; nothing to read logs from."));

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{Agent_A_Id:N}/logs", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetLogs_WhenDeployed_ReturnsWorkerReportedTail()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Id == Agent_A_Id), Arg.Any<CancellationToken>())
            .Returns(AgentEntry(Agent_A_Id));

        _factory.PersistentAgentExecutionGateway
            .GetLogsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PersistentAgentLogsState(
                AgentId: Actor1_Id.ToString("N"),
                ContainerId: "container-xyz",
                Tail: 200,
                Logs: "line one\nline two"));

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{Agent_A_Id:N}/logs", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<PersistentAgentLogsResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.ContainerId.ShouldBe("container-xyz");
        body.Logs.ShouldBe("line one\nline two");
    }
}
