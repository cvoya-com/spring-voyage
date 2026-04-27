// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Endpoint-level coverage for the persistent-agent lifecycle surface (#396).
/// Focuses on the wire contract: 404 shape when the agent does not exist,
/// idempotent undeploy, canonical empty-deployment shape, and the
/// extended-status response carrying the optional deployment block.
/// The happy-path deploy requires a runnable container and is exercised by
/// <c>PersistentAgentLifecycleTests</c> + the integration tests in
/// <c>tests/Cvoya.Spring.Dapr.Tests/Execution/</c>.
/// </summary>
public class PersistentAgentEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
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

    [Fact]
    public async Task Deploy_WhenAgentNotInDirectory_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Path == "ghost"), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agents/ghost/deploy", new DeployPersistentAgentRequest(), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Undeploy_IsIdempotentForAgentThatIsNotDeployed()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Path == "idle"), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("agent", "idle"),
                "actor-1",
                "Idle",
                "",
                null,
                DateTimeOffset.UtcNow));

        var response = await _client.PostAsync("/api/v1/tenant/agents/idle/undeploy", content: null, ct);

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
            .ResolveAsync(Arg.Is<Address>(a => a.Path == "a"), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("agent", "a"),
                "actor-1",
                "A",
                "",
                null,
                DateTimeOffset.UtcNow));

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agents/a/scale",
            new ScalePersistentAgentRequest(2),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetDeployment_WhenAgentExistsButNotDeployed_ReturnsEmptyShape()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Path == "a"), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("agent", "a"),
                "actor-1",
                "A",
                "",
                null,
                DateTimeOffset.UtcNow));

        var response = await _client.GetAsync("/api/v1/tenant/agents/a/deployment", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<PersistentAgentDeploymentResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.AgentId.ShouldBe("a");
        body.Running.ShouldBeFalse();
        body.Replicas.ShouldBe(0);
    }

    [Fact]
    public async Task GetLogs_WhenAgentNotDeployed_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Path == "a"), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("agent", "a"),
                "actor-1",
                "A",
                "",
                null,
                DateTimeOffset.UtcNow));

        var response = await _client.GetAsync("/api/v1/tenant/agents/a/logs", ct);

        // The lifecycle service throws SpringException when there's no entry
        // and the endpoint translates that into a 404 so the CLI surfaces a
        // clear "no deployment" message.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}