// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the agent lifecycle endpoints added in #2364 /
/// PR #2371:
/// <c>POST /api/v1/tenant/agents/{id}/start</c>,
/// <c>POST /api/v1/tenant/agents/{id}/stop</c>, and
/// <c>POST /api/v1/tenant/agents/{id}/revalidate</c>.
/// Mirrors <see cref="UnitLifecycleEndpointTests"/> +
/// <see cref="UnitRevalidateEndpointTests"/> for the agent surface.
/// </summary>
public class AgentLifecycleEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid AgentId_Guid = new("00007722-bbbb-cccc-dddd-000000000000");
    private static readonly string AgentId = AgentId_Guid.ToString("N");

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AgentLifecycleEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ──────────────────────────────────────────────────────────────────────
    // POST /start
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAgent_HappyPath_Returns202()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = ArrangeAgent(
            startingResult: new TransitionResult(true, LifecycleStatus.Starting, null),
            finalResult: new TransitionResult(true, LifecycleStatus.Running, null));

        var response = await _client.PostAsync($"/api/v1/tenant/agents/{AgentId}/start", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Starting, Arg.Any<CancellationToken>());
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Running, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAgent_NotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeAgent(found: false);

        var response = await _client.PostAsync($"/api/v1/tenant/agents/{AgentId}/start", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StartAgent_StartingTransitionRejected_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IAgentActor>();
        proxy.TransitionAsync(LifecycleStatus.Starting, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(false, LifecycleStatus.Running, "cannot transition from Running to Starting"));
        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/agents/{AgentId}/start", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        // Second transition must NOT fire if first rejected.
        await proxy.DidNotReceive().TransitionAsync(LifecycleStatus.Running, Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // POST /stop
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StopAgent_HappyPath_Returns202()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IAgentActor>();
        proxy.TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopping, null));
        proxy.TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopped, null));
        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/agents/{AgentId}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>());
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAgent_NotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeAgent(found: false);

        var response = await _client.PostAsync($"/api/v1/tenant/agents/{AgentId}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StopAgent_AlreadyStopped_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IAgentActor>();
        proxy.TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(false, LifecycleStatus.Stopped, "cannot transition from Stopped to Stopping"));
        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/agents/{AgentId}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ──────────────────────────────────────────────────────────────────────
    // POST /revalidate
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LifecycleStatus.Draft)]
    [InlineData(LifecycleStatus.Error)]
    [InlineData(LifecycleStatus.Stopped)]
    public async Task RevalidateAgent_AllowedFromState_Returns202(LifecycleStatus current)
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IAgentActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(current);
        proxy.TransitionAsync(LifecycleStatus.Validating, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Validating, null));
        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/agents/{AgentId}/revalidate", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Validating, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(LifecycleStatus.Running)]
    [InlineData(LifecycleStatus.Starting)]
    [InlineData(LifecycleStatus.Stopping)]
    [InlineData(LifecycleStatus.Validating)]
    public async Task RevalidateAgent_DisallowedFromState_Returns409(LifecycleStatus current)
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IAgentActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(current);
        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/agents/{AgentId}/revalidate", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        // Transition must NOT fire from a disallowed source state.
        await proxy.DidNotReceive().TransitionAsync(LifecycleStatus.Validating, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevalidateAgent_NotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeAgent(found: false);

        var response = await _client.PostAsync($"/api/v1/tenant/agents/{AgentId}/revalidate", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Arrangement helpers
    // ──────────────────────────────────────────────────────────────────────

    private IAgentActor ArrangeAgent(TransitionResult startingResult, TransitionResult finalResult)
    {
        var proxy = Substitute.For<IAgentActor>();
        proxy.TransitionAsync(LifecycleStatus.Starting, Arg.Any<CancellationToken>()).Returns(startingResult);
        proxy.TransitionAsync(LifecycleStatus.Running, Arg.Any<CancellationToken>()).Returns(finalResult);
        ArrangeResolved(proxy);
        return proxy;
    }

    private void ArrangeAgent(bool found)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();

        if (found)
        {
            var entry = new DirectoryEntry(
                new Address("agent", AgentId_Guid),
                AgentId_Guid,
                "Test Agent",
                "A test agent",
                null,
                DateTimeOffset.UtcNow);
            _factory.DirectoryService
                .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == AgentId_Guid), Arg.Any<CancellationToken>())
                .Returns(entry);
        }
        else
        {
            _factory.DirectoryService
                .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == AgentId_Guid), Arg.Any<CancellationToken>())
                .Returns((DirectoryEntry?)null);
        }
    }

    private void ArrangeResolved(IAgentActor proxy)
    {
        ArrangeAgent(found: true);
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(Arg.Is<global::Dapr.Actors.ActorId>(a => a.GetId() == AgentId), Arg.Any<string>())
            .Returns(proxy);
    }
}
