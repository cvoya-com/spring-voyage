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
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Endpoint coverage for <c>GET /api/v1/tenant/units/{id}/runtime-status</c>
/// (#2100). Mirrors <c>AgentEndpointsTests</c> coverage: idle / busy /
/// queued / unavailable, plus the 404 path. Units project the same
/// shape as agents (per ADR-0017 a unit is an agent).
/// </summary>
public class UnitRuntimeStatusEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitRuntimeStatusEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetUnitRuntimeStatus_UnitNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var ghost = Guid.NewGuid();
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Id == ghost), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{ghost:N}/runtime-status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUnitRuntimeStatus_NoRegistryEntry_ReturnsIdle()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeUnitDirectoryEntry(unitId, "Healthy Unit");
        ArrangeUnitRuntimeStatus(unitId, inFlight: 0, queued: 0);
        // No registry registration for a unit ⇒ TryGet returns false ⇒
        // we still report idle (not unavailable) — units don't have a
        // hosting-mode slot and a Draft / Stopped unit shouldn't scream.

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{unitId:N}/runtime-status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<AgentRuntimeStatusResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("idle");
    }

    [Fact]
    public async Task GetUnitRuntimeStatus_DeploymentUnhealthy_ReturnsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeUnitDirectoryEntry(unitId, "Crashed Unit");

        // ADR-0052 / #2618: deployment health is read from the execution host
        // over the gateway. A running-but-unhealthy deployment projects to
        // `unavailable`.
        var actorId = unitId.ToString("N");
        _factory.PersistentAgentExecutionGateway
            .GetDeploymentAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new PersistentAgentDeploymentState(
                AgentId: actorId,
                Running: true,
                HealthStatus: "unhealthy",
                Image: null,
                Endpoint: "http://test/unit",
                ContainerId: "container-unit",
                StartedAt: DateTimeOffset.UtcNow,
                ConsecutiveFailures: 3));

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{unitId:N}/runtime-status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<AgentRuntimeStatusResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("unavailable");

        // Reset the shared substitute so a sibling test doesn't see this entry.
        _factory.PersistentAgentExecutionGateway
            .GetDeploymentAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(PersistentAgentDeploymentState.NotRunning(actorId));
    }

    private void ArrangeUnitDirectoryEntry(Guid unitId, string displayName)
    {
        var entry = new DirectoryEntry(
            new Address("unit", unitId),
            unitId,
            displayName,
            displayName,
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == unitId),
                Arg.Any<CancellationToken>())
            .Returns(entry);
    }

    private void ArrangeUnitRuntimeStatus(Guid unitId, int inFlight, int queued)
    {
        var actorIdString = unitId.ToString("N");
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetRuntimeStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new AgentRuntimeStatusReport(
                InFlightThreadCount: inFlight,
                QueuedMessageCount: queued,
                ChannelCount: 0,
                ObservedAt: DateTimeOffset.UtcNow));
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == actorIdString),
                Arg.Any<string>())
            .Returns(proxy);
    }
}
