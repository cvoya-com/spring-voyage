// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Text.Json;

using Cvoya.Spring.Connectors;
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
/// Integration tests for <see cref="UnitEndpoints"/> lifecycle routes
/// (<c>POST /api/v1/units/{id}/start</c> and <c>POST /api/v1/units/{id}/stop</c>).
///
/// Since #371 the start/stop endpoints no longer shell out to the legacy
/// per-unit container surface. The ephemeral per-conversation container
/// lifecycle is owned by the A2A dispatcher (#346/#349). Persistent-agent
/// deployments are torn down by /stop and /force-delete iterating the
/// unit's members (#2397).
/// </summary>
public class UnitLifecycleEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid ActorEngineering_Id = new("00002711-bbbb-cccc-dddd-000000000000");

    private const string UnitDisplayName = "engineering";
    private static readonly Guid ActorId_Guid = ActorEngineering_Id;
    private static readonly string ActorId = ActorId_Guid.ToString("N");
    // Post-#1629 URL paths carry the unit's Guid hex.
    private static readonly string UnitName = ActorId;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitLifecycleEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task StartUnit_HappyPath_Returns202AndTransitionsToRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = ArrangeUnit(startingResult: new TransitionResult(true, LifecycleStatus.Starting, null),
            finalResult: new TransitionResult(true, LifecycleStatus.Running, null));

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/start", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await proxy.Received(1).TransitionAsync(LifecycleStatus.Starting, Arg.Any<CancellationToken>());
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Running, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartUnit_AlreadyRunning_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(LifecycleStatus.Starting, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(false, LifecycleStatus.Running, "cannot transition from Running to Starting"));

        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/start", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task StartUnit_NonGuidId_Returns404WithoutThrowing()
    {
        // #2981 §4: a non-Guid id (e.g. a slug) must surface as a clean 404
        // rather than an ungraceful error from Address.For throwing
        // InvalidAddressIdException on the raw id. Mirrors GetUnitAsync's
        // validate-first guard so /start behaves like its sibling endpoints.
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsync("/api/v1/tenant/units/not-a-guid/start", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StopUnit_HappyPath_Returns202AndTransitionsToStopped()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopping, null));
        proxy.TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopped, null));

        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await proxy.Received(1).TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>());
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartUnit_BoundToConnector_InvokesConnectorStartHook()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = ArrangeUnit(startingResult: new TransitionResult(true, LifecycleStatus.Starting, null),
            finalResult: new TransitionResult(true, LifecycleStatus.Running, null));

        // ADR-0040 / #2050: the binding lookup goes through
        // IUnitConnectorConfigStore (EF), not the unit actor proxy.
        var boundTypeId = _factory.StubConnectorType.TypeId;
        var boundConfig = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "platform" });
        _factory.ConnectorConfigStore.GetAsync(UnitName, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(boundTypeId, boundConfig));

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/start", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _factory.StubConnectorType.Received(1)
            .OnUnitStartingAsync(UnitName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartUnit_ConnectorStartHookThrows_StillTransitionsToRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = ArrangeUnit(startingResult: new TransitionResult(true, LifecycleStatus.Starting, null),
            finalResult: new TransitionResult(true, LifecycleStatus.Running, null));

        var boundTypeId = _factory.StubConnectorType.TypeId;
        var boundConfig = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "platform" });
        _factory.ConnectorConfigStore.GetAsync(UnitName, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(boundTypeId, boundConfig));

        _factory.StubConnectorType.OnUnitStartingAsync(UnitName, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("external 502"));

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/start", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Running, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_BoundToConnector_InvokesConnectorStopHook()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopping, null));
        proxy.TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopped, null));
        var boundTypeId = _factory.StubConnectorType.TypeId;
        var boundConfig = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "platform" });
        _factory.ConnectorConfigStore.GetAsync(UnitName, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(boundTypeId, boundConfig));

        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _factory.StubConnectorType.Received(1)
            .OnUnitStoppingAsync(UnitName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_Unbound_DoesNotInvokeConnectorHooks()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopping, null));
        proxy.TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopped, null));
        _factory.ConnectorConfigStore.GetAsync(UnitName, Arg.Any<CancellationToken>())
            .Returns((UnitConnectorBinding?)null);

        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _factory.StubConnectorType.DidNotReceive()
            .OnUnitStoppingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_PersistentAgentMembers_StopsEachPreservingVolumeBeforeStopped()
    {
        // #2397: /stop must iterate the unit's members and tear down each
        // persistent-agent container + Dapr sidecar so they do not survive
        // Stopped. #2999: a unit stop is RESUMABLE, so each member is stopped
        // via the volume-preserving StopAgentContainerAsync (durable memory
        // survives), NOT the volume-reclaiming UndeployAsync. The /stop
        // endpoint never drives unit-container lifecycle itself (#371).
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopping, null));
        proxy.TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopped, null));

        ArrangeResolved(proxy);

        var agentA = new Guid("aaaa1111-0000-0000-0000-000000000001");
        var agentB = new Guid("aaaa1111-0000-0000-0000-000000000002");

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
        _factory.ExecutionHostGateway.ClearReceivedCalls();

        var response = await client.PostAsync($"/api/v1/tenant/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await membershipRepo.Received(1)
            .ListByUnitAsync(ActorId_Guid, Arg.Any<CancellationToken>());

        // #2999: each member (2) + the unit-as-agent router (1) is torn down
        // via the volume-PRESERVING verb; the volume-reclaiming UndeployAsync
        // is never called on a resumable stop.
        await _factory.ExecutionHostGateway.Received(3)
            .StopAgentContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.ExecutionHostGateway.DidNotReceive()
            .UndeployAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The unit still transitions through Stopping → Stopped after the
        // member teardown.
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>());
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_PersistentAgentMembersLookupThrows_StillTransitionsToStopped()
    {
        // The member-teardown step is best-effort: a hard failure in the
        // membership lookup is logged and swallowed so the unit still
        // reaches Stopped. The operator's recovery path is /force-delete
        // for leaked containers (#2397).
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopping, null));
        proxy.TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopped, null));

        ArrangeResolved(proxy);

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

        var response = await client.PostAsync($"/api/v1/tenant/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_StopsUnitAsAgentRouterPreservingVolume()
    {
        // #2708: a unit-as-agent (ADR-0039) runs its own router runtime
        // under the unit's id. The members cascade does not enumerate this
        // runtime — it is not in the unit's members collection — so /stop
        // must tear down the router container against the unit's own id.
        // #2999: a unit stop is RESUMABLE, so the router is stopped via the
        // volume-preserving StopAgentContainerAsync (its workspace volume +
        // credentials survive for a later /start), NOT the volume-reclaiming
        // UndeployAsync. The gateway is idempotent: a no-op for ephemeral
        // units, so this fires unconditionally.
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopping, null));
        proxy.TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopped, null));

        ArrangeResolved(proxy);
        _factory.ExecutionHostGateway.ClearReceivedCalls();

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _factory.ExecutionHostGateway.Received(1)
            .StopAgentContainerAsync(ActorId, Arg.Any<CancellationToken>());
        await _factory.ExecutionHostGateway.DidNotReceive()
            .UndeployAsync(ActorId, Arg.Any<CancellationToken>());
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_UnitAsAgentRouterStopThrows_StillTransitionsToStopped()
    {
        // The router-teardown step is best-effort, same shape as the
        // members teardown (#2397) — a hard failure is logged and
        // swallowed so the unit still reaches Stopped. The operator's
        // recovery path is /force-delete for leaked containers (#2708).
        // #2999: a resumable stop tears the router down via the
        // volume-preserving StopAgentContainerAsync.
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopping, null));
        proxy.TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Stopped, null));

        ArrangeResolved(proxy);
        _factory.ExecutionHostGateway.ClearReceivedCalls();
        _factory.ExecutionHostGateway
            .StopAgentContainerAsync(ActorId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("worker offline"));

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await proxy.Received(1).TransitionAsync(LifecycleStatus.Stopped, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_AlreadyStopped_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(LifecycleStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(false, LifecycleStatus.Stopped, "cannot transition from Stopped to Stopping"));

        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private IUnitActor ArrangeUnit(TransitionResult startingResult, TransitionResult finalResult)
    {
        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(LifecycleStatus.Starting, Arg.Any<CancellationToken>()).Returns(startingResult);
        proxy.TransitionAsync(LifecycleStatus.Running, Arg.Any<CancellationToken>()).Returns(finalResult);
        ArrangeResolved(proxy);
        return proxy;
    }

    private void ArrangeResolved(IUnitActor proxy)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.StubConnectorType.ClearReceivedCalls();
        _factory.ConnectorConfigStore.ClearReceivedCalls();
        _factory.ExecutionHostGateway.ClearReceivedCalls();

        // #2708: reset the unit-as-agent router undeploy stub to success
        // — the partial-failure test below overrides it and the
        // arrangement would otherwise leak across tests (xUnit
        // IClassFixture shares the factory).
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

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<global::Dapr.Actors.ActorId>(a => a.GetId() == ActorId), Arg.Any<string>())
            .Returns(proxy);
    }
}
