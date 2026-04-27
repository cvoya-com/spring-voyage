// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <c>DELETE /api/v1/units/{id}</c>. The endpoint must
/// refuse deletion while the unit is Running/Starting/Stopping/Error (#116) so
/// the container, sidecar, and network are never orphaned.
/// </summary>
public class UnitDeleteEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string UnitName = "engineering";
    private const string ActorId = "actor-engineering";

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
        ArrangeUnit(UnitStatus.Stopped);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{UnitName}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteUnit_Draft_Returns204AndUnregisters()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeUnit(UnitStatus.Draft);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{UnitName}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(UnitStatus.Running)]
    [InlineData(UnitStatus.Starting)]
    [InlineData(UnitStatus.Stopping)]
    [InlineData(UnitStatus.Error)]
    public async Task DeleteUnit_NotStopped_Returns409AndDoesNotUnregister(UnitStatus status)
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
        ArrangeUnit(UnitStatus.Error);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{UnitName}?force=true", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.UnitContainerLifecycle.Received(1).StopUnitAsync(
            ActorId, Arg.Any<CancellationToken>());
        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName),
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
        ArrangeUnit(UnitStatus.Error);

        _factory.UnitContainerLifecycle
            .StopUnitAsync(ActorId, Arg.Any<CancellationToken>())
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
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName),
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
        ArrangeUnit(UnitStatus.Stopped);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{UnitName}?force=true", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.UnitContainerLifecycle.DidNotReceive().StopUnitAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.ActivityEventBus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
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

        var response = await _client.DeleteAsync("/api/v1/tenant/units/does-not-exist", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private void ArrangeUnit(UnitStatus status)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.UnitContainerLifecycle.ClearReceivedCalls();
        _factory.GitHubWebhookRegistrar.ClearReceivedCalls();
        _factory.ActivityEventBus.ClearReceivedCalls();

        // Reset the container stop stub to success; individual tests override
        // it when they want to exercise the partial-failure path.
        _factory.UnitContainerLifecycle
            .StopUnitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

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

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(status);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<ActorId>(a => a.GetId() == ActorId), Arg.Any<string>())
            .Returns(proxy);
    }
}