// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <see cref="UnitEndpoints"/> lifecycle routes
/// (<c>POST /api/v1/units/{id}/start</c> and <c>POST /api/v1/units/{id}/stop</c>).
///
/// Since #371 the start/stop endpoints no longer shell out to a container
/// runtime — they simply transition the unit actor through its state machine.
/// Agent-container lifecycle is managed by the A2A dispatcher (#346/#349).
/// </summary>
public class UnitLifecycleEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string UnitName = "engineering";
    private const string ActorId = "actor-engineering";

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
        var proxy = ArrangeUnit(startingResult: new TransitionResult(true, UnitStatus.Starting, null),
            finalResult: new TransitionResult(true, UnitStatus.Running, null));

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/start", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await proxy.Received(1).TransitionAsync(UnitStatus.Starting, Arg.Any<CancellationToken>());
        await proxy.Received(1).TransitionAsync(UnitStatus.Running, Arg.Any<CancellationToken>());

        // Container lifecycle must NOT be invoked — #371.
        await _factory.UnitContainerLifecycle.DidNotReceive()
            .StartUnitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartUnit_AlreadyRunning_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(UnitStatus.Starting, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(false, UnitStatus.Running, "cannot transition from Running to Starting"));

        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/start", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await _factory.UnitContainerLifecycle.DidNotReceive()
            .StartUnitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_HappyPath_Returns202AndTransitionsToStopped()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(UnitStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Stopping, null));
        proxy.TransitionAsync(UnitStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Stopped, null));

        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await proxy.Received(1).TransitionAsync(UnitStatus.Stopping, Arg.Any<CancellationToken>());
        await proxy.Received(1).TransitionAsync(UnitStatus.Stopped, Arg.Any<CancellationToken>());

        // Container lifecycle must NOT be invoked — #371.
        await _factory.UnitContainerLifecycle.DidNotReceive()
            .StopUnitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartUnit_BoundToConnector_InvokesConnectorStartHook()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = ArrangeUnit(startingResult: new TransitionResult(true, UnitStatus.Starting, null),
            finalResult: new TransitionResult(true, UnitStatus.Running, null));

        var boundTypeId = _factory.StubConnectorType.TypeId;
        var boundConfig = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "platform" });
        proxy.GetConnectorBindingAsync(Arg.Any<CancellationToken>())
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
        var proxy = ArrangeUnit(startingResult: new TransitionResult(true, UnitStatus.Starting, null),
            finalResult: new TransitionResult(true, UnitStatus.Running, null));

        var boundTypeId = _factory.StubConnectorType.TypeId;
        var boundConfig = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "platform" });
        proxy.GetConnectorBindingAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(boundTypeId, boundConfig));

        _factory.StubConnectorType.OnUnitStartingAsync(UnitName, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("external 502"));

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/start", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await proxy.Received(1).TransitionAsync(UnitStatus.Running, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_BoundToConnector_InvokesConnectorStopHook()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(UnitStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Stopping, null));
        proxy.TransitionAsync(UnitStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Stopped, null));
        var boundTypeId = _factory.StubConnectorType.TypeId;
        var boundConfig = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "platform" });
        proxy.GetConnectorBindingAsync(Arg.Any<CancellationToken>())
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
        proxy.TransitionAsync(UnitStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Stopping, null));
        proxy.TransitionAsync(UnitStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Stopped, null));
        proxy.GetConnectorBindingAsync(Arg.Any<CancellationToken>())
            .Returns((UnitConnectorBinding?)null);

        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _factory.StubConnectorType.DidNotReceive()
            .OnUnitStoppingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_AlreadyStopped_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(UnitStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(false, UnitStatus.Stopped, "cannot transition from Stopped to Stopping"));

        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await _factory.UnitContainerLifecycle.DidNotReceive()
            .StopUnitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private IUnitActor ArrangeUnit(TransitionResult startingResult, TransitionResult finalResult)
    {
        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(UnitStatus.Starting, Arg.Any<CancellationToken>()).Returns(startingResult);
        proxy.TransitionAsync(UnitStatus.Running, Arg.Any<CancellationToken>()).Returns(finalResult);
        ArrangeResolved(proxy);
        return proxy;
    }

    private void ArrangeResolved(IUnitActor proxy)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.UnitContainerLifecycle.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.StubConnectorType.ClearReceivedCalls();

        var entry = new DirectoryEntry(
            new Address("unit", UnitName),
            ActorId,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName), Arg.Any<CancellationToken>())
            .Returns(entry);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<ActorId>(a => a.GetId() == ActorId), Arg.Any<string>())
            .Returns(proxy);
    }
}