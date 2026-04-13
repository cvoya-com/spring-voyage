// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using FluentAssertions;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Xunit;

/// <summary>
/// Integration tests for <see cref="UnitEndpoints"/> lifecycle routes
/// (<c>POST /api/v1/units/{id}/start</c> and <c>POST /api/v1/units/{id}/stop</c>).
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

        _factory.UnitContainerLifecycle.StartUnitAsync(ActorId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PostAsync($"/api/v1/units/{UnitName}/start", content: null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await proxy.Received(1).TransitionAsync(UnitStatus.Starting, Arg.Any<CancellationToken>());
        await proxy.Received(1).TransitionAsync(UnitStatus.Running, Arg.Any<CancellationToken>());
        await _factory.UnitContainerLifecycle.Received(1)
            .StartUnitAsync(ActorId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartUnit_AlreadyRunning_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(UnitStatus.Starting, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(false, UnitStatus.Running, "cannot transition from Running to Starting"));

        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/units/{UnitName}/start", content: null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await _factory.UnitContainerLifecycle.DidNotReceive()
            .StartUnitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartUnit_ContainerFails_Returns500AndTransitionsToError()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(UnitStatus.Starting, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Starting, null));
        proxy.TransitionAsync(UnitStatus.Error, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Error, null));

        ArrangeResolved(proxy);

        _factory.UnitContainerLifecycle.StartUnitAsync(ActorId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("podman unreachable"));

        var response = await _client.PostAsync($"/api/v1/units/{UnitName}/start", content: null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        await proxy.Received(1).TransitionAsync(UnitStatus.Error, Arg.Any<CancellationToken>());
        await proxy.DidNotReceive().TransitionAsync(UnitStatus.Running, Arg.Any<CancellationToken>());
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

        _factory.UnitContainerLifecycle.StopUnitAsync(ActorId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PostAsync($"/api/v1/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await proxy.Received(1).TransitionAsync(UnitStatus.Stopping, Arg.Any<CancellationToken>());
        await proxy.Received(1).TransitionAsync(UnitStatus.Stopped, Arg.Any<CancellationToken>());
        await _factory.UnitContainerLifecycle.Received(1)
            .StopUnitAsync(ActorId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartUnit_WithGitHubConfig_RegistersWebhookAndPersistsHookId()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = ArrangeUnit(startingResult: new TransitionResult(true, UnitStatus.Starting, null),
            finalResult: new TransitionResult(true, UnitStatus.Running, null));

        proxy.GetGitHubConfigAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitGitHubConfig("acme", "platform"));

        _factory.UnitContainerLifecycle.StartUnitAsync(ActorId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _factory.GitHubWebhookRegistrar.RegisterAsync("acme", "platform", Arg.Any<CancellationToken>())
            .Returns(12345L);

        var response = await _client.PostAsync($"/api/v1/units/{UnitName}/start", content: null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await _factory.GitHubWebhookRegistrar.Received(1)
            .RegisterAsync("acme", "platform", Arg.Any<CancellationToken>());
        await proxy.Received(1).SetGitHubHookIdAsync(12345L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartUnit_WithGitHubConfig_WebhookFailure_StillTransitionsToRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = ArrangeUnit(startingResult: new TransitionResult(true, UnitStatus.Starting, null),
            finalResult: new TransitionResult(true, UnitStatus.Running, null));

        proxy.GetGitHubConfigAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitGitHubConfig("acme", "platform"));

        _factory.UnitContainerLifecycle.StartUnitAsync(ActorId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _factory.GitHubWebhookRegistrar.RegisterAsync("acme", "platform", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("github 502"));

        var response = await _client.PostAsync($"/api/v1/units/{UnitName}/start", content: null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await proxy.DidNotReceive().SetGitHubHookIdAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await proxy.Received(1).TransitionAsync(UnitStatus.Running, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_WithPersistedHookId_UnregistersWebhookAndClearsHookId()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(UnitStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Stopping, null));
        proxy.TransitionAsync(UnitStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Stopped, null));
        proxy.GetGitHubConfigAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitGitHubConfig("acme", "platform"));
        proxy.GetGitHubHookIdAsync(Arg.Any<CancellationToken>())
            .Returns(12345L);

        ArrangeResolved(proxy);

        _factory.UnitContainerLifecycle.StopUnitAsync(ActorId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PostAsync($"/api/v1/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _factory.GitHubWebhookRegistrar.Received(1)
            .UnregisterAsync("acme", "platform", 12345L, Arg.Any<CancellationToken>());
        await proxy.Received(1).SetGitHubHookIdAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_WithoutHookId_DoesNotInvokeRegistrar()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(UnitStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Stopping, null));
        proxy.TransitionAsync(UnitStatus.Stopped, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Stopped, null));
        proxy.GetGitHubConfigAsync(Arg.Any<CancellationToken>())
            .Returns((UnitGitHubConfig?)null);
        proxy.GetGitHubHookIdAsync(Arg.Any<CancellationToken>())
            .Returns((long?)null);

        ArrangeResolved(proxy);

        _factory.UnitContainerLifecycle.StopUnitAsync(ActorId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PostAsync($"/api/v1/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _factory.GitHubWebhookRegistrar.DidNotReceive()
            .UnregisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnit_AlreadyStopped_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.TransitionAsync(UnitStatus.Stopping, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(false, UnitStatus.Stopped, "cannot transition from Stopped to Stopping"));

        ArrangeResolved(proxy);

        var response = await _client.PostAsync($"/api/v1/units/{UnitName}/stop", content: null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

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
        _factory.GitHubWebhookRegistrar.ClearReceivedCalls();

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