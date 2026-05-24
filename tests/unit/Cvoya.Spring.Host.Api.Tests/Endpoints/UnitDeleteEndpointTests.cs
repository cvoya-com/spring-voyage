// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <c>DELETE /api/v1/units/{id}</c>. The endpoint must
/// refuse deletion while the unit is Running/Starting/Stopping/Error (#116) so
/// the container, sidecar, and network are never orphaned.
/// </summary>
public class UnitDeleteEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid ActorEngineering_Id = new("00002711-bbbb-cccc-dddd-000000000000");

    private const string UnitDisplayName = "engineering";
    private static readonly Guid ActorId_Guid = ActorEngineering_Id;
    private static readonly string ActorId = ActorId_Guid.ToString("N");
    // Post-#1629 URL paths carry the unit's Guid hex.
    private static readonly string UnitName = ActorId;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitDeleteEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task DeleteUnit_Stopped_Returns204AndUnregisters()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnit(LifecycleStatus.Stopped);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{UnitName}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ActorId_Guid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUnit_Draft_Returns204AndUnregisters()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnit(LifecycleStatus.Draft);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{UnitName}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ActorId_Guid),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(LifecycleStatus.Running)]
    [InlineData(LifecycleStatus.Starting)]
    [InlineData(LifecycleStatus.Stopping)]
    [InlineData(LifecycleStatus.Error)]
    public async Task DeleteUnit_NotStopped_Returns409AndDoesNotUnregister(LifecycleStatus status)
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnit(status);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{UnitName}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await _factory.DirectoryService.DidNotReceive().UnregisterAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUnit_Force_FromError_Returns204AndTearsDownAndEmitsEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnit(LifecycleStatus.Error);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{UnitName}?force=true", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // #2627: unit-container teardown is delegated to the worker over the
        // execution gateway — the API host no longer drives it in-process.
        await _factory.ExecutionHostGateway.Received(1)
            .StopUnitContainerAsync(ActorId, Arg.Any<CancellationToken>());
        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ActorId_Guid),
            Arg.Any<CancellationToken>());
        await _factory.ActivityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Severity == ActivitySeverity.Info &&
                e.Summary.Contains("Force-deleted") &&
                e.Source.Path == UnitName),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUnit_Force_ContainerStopFails_Returns200WithFailuresAndStillUnregisters()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnit(LifecycleStatus.Error);

        _factory.ExecutionHostGateway
            .StopUnitContainerAsync(ActorId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("container already gone")));

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{UnitName}?force=true", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("forceDeleted").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("teardownFailures")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ShouldContain("container");

        // Directory entry removal still happens even if the container step failed —
        // that's the whole point of force-delete.
        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ActorId_Guid),
            Arg.Any<CancellationToken>());

        // Event surfaces the failure as Warning.
        await _factory.ActivityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.StateChanged &&
                e.Severity == ActivitySeverity.Warning),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUnit_Force_FromStopped_SkipsTeardown()
    {
        // When the unit is already in a clean state, ?force=true should not invoke
        // teardown — the fast path still applies.
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnit(LifecycleStatus.Stopped);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{UnitName}?force=true", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.ExecutionHostGateway.DidNotReceive()
            .StopUnitContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.ActivityEventBus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUnit_Force_UndeploysUnitAsAgentRouterAndReturns204()
    {
        // #2708: a unit-as-agent (ADR-0039) runs its own router runtime
        // under the unit's id. The members cascade does not enumerate
        // this runtime — it is not in the unit's members collection — so
        // force-delete must call UndeployAsync against the unit's own id
        // to drop the router container + workspace volume (which still
        // holds the agent's live credentials). The gateway is idempotent:
        // a no-op for ephemeral units, so this fires unconditionally.
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnit(LifecycleStatus.Error);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{UnitName}?force=true", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.ExecutionHostGateway.Received(1)
            .UndeployAsync(ActorId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUnit_Force_UnitAsAgentRouterUndeployFails_RecordsFailureAndStillUnregisters()
    {
        // The router-teardown step is best-effort: a failure is logged
        // and recorded as a "persistent-agent-router" entry in
        // teardownFailures but does not block directory unregister —
        // matching the existing container-step failure behavior (#147).
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnit(LifecycleStatus.Error);

        _factory.ExecutionHostGateway
            .UndeployAsync(ActorId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("worker offline"));

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{UnitName}?force=true", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("forceDeleted").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("teardownFailures")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ShouldContain("persistent-agent-router");

        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ActorId_Guid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUnit_Force_PersistentAgentMembers_UndeploysEachAndReturns204()
    {
        // #2397: force-delete must iterate the unit's members and undeploy
        // each persistent-agent deployment alongside the other best-effort
        // teardown steps.
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnit(LifecycleStatus.Error);

        var agentA = new Guid("bbbb2222-0000-0000-0000-000000000001");
        var agentB = new Guid("bbbb2222-0000-0000-0000-000000000002");

        var membershipRepo = Substitute.For<IUnitMembershipRepository>();
        membershipRepo.ListByUnitAsync(ActorId_Guid, Arg.Any<CancellationToken>())
            .Returns(new List<UnitMembership>
            {
                new(ActorId_Guid, agentA, Enabled: true),
                new(ActorId_Guid, agentB, Enabled: true),
            });

        using var probingFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(IUnitMembershipRepository))
                    .ToList();
                foreach (var d in existing)
                {
                    services.Remove(d);
                }
                services.AddSingleton(membershipRepo);
            });
        });
        var client = probingFactory.CreateClient();

        var response = await client.DeleteAsync($"/api/v1/tenant/units/{UnitName}?force=true", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await membershipRepo.Received(1)
            .ListByUnitAsync(ActorId_Guid, Arg.Any<CancellationToken>());
        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ActorId_Guid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUnit_Force_PersistentAgentMembersLookupThrows_RecordsFailureAndStillUnregisters()
    {
        // A best-effort step failure surfaces in the response's
        // teardownFailures list but does not block the directory unregister
        // — matching the existing container-step failure behavior (#147).
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnit(LifecycleStatus.Error);

        var membershipRepo = Substitute.For<IUnitMembershipRepository>();
        membershipRepo.ListByUnitAsync(ActorId_Guid, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));

        using var probingFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(IUnitMembershipRepository))
                    .ToList();
                foreach (var d in existing)
                {
                    services.Remove(d);
                }
                services.AddSingleton(membershipRepo);
            });
        });
        var client = probingFactory.CreateClient();

        var response = await client.DeleteAsync($"/api/v1/tenant/units/{UnitName}?force=true", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("forceDeleted").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("teardownFailures")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ShouldContain("persistent-agent-members");

        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ActorId_Guid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUnit_Unknown_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{Guid.NewGuid():N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private void ArrangeUnit(LifecycleStatus status)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.ExecutionHostGateway.ClearReceivedCalls();
        _factory.ActivityEventBus.ClearReceivedCalls();

        // Reset the container-teardown stub to success; individual tests
        // override it when they want to exercise the partial-failure path.
        _factory.ExecutionHostGateway
            .StopUnitContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // #2708: same reset for the unit-as-agent router undeploy stub —
        // the partial-failure test below overrides it and the arrangement
        // would otherwise leak across tests (xUnit IClassFixture shares
        // the factory).
        _factory.ExecutionHostGateway
            .UndeployAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                Cvoya.Spring.Dapr.Execution.PersistentAgentDeploymentState.NotRunning(
                    callInfo.ArgAt<string>(0)));

        var entry = new DirectoryEntry(
            new Address("unit", ActorId_Guid),
            ActorId_Guid,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ActorId_Guid), Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(status);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<global::Dapr.Actors.ActorId>(a => a.GetId() == ActorId), Arg.Any<string>())
            .Returns(proxy);
    }
}
