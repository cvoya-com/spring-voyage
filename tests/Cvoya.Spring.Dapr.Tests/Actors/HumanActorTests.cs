// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using FluentAssertions;
using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

/// <summary>
/// Unit tests for <see cref="HumanActor"/> covering message routing,
/// status queries, health checks, permission enforcement, and state management.
/// </summary>
public class HumanActorTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly HumanActor _actor;

    public HumanActorTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var host = ActorHost.CreateForTest<HumanActor>(new ActorTestOptions
        {
            ActorId = new ActorId("test-human")
        });
        _actor = new HumanActor(host, _loggerFactory);
        SetStateManager(_actor, _stateManager);

        // Default: no state stored (defaults to Viewer permission).
        _stateManager.TryGetStateAsync<PermissionLevel>(StateKeys.HumanPermission, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<PermissionLevel>(false, default));
        _stateManager.TryGetStateAsync<string>(StateKeys.HumanIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(false, default!));
    }

    private static Message CreateMessage(
        MessageType type = MessageType.Domain,
        string? conversationId = null,
        JsonElement? payload = null)
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", "test-sender"),
            new Address("human", "test-human"),
            type,
            conversationId ?? Guid.NewGuid().ToString(),
            payload ?? JsonSerializer.SerializeToElement(new { }),
            DateTimeOffset.UtcNow);
    }

    private static void SetStateManager(Actor actor, IActorStateManager stateManager)
    {
        var field = typeof(Actor).GetField("<StateManager>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (field is not null)
        {
            field.SetValue(actor, stateManager);
        }
        else
        {
            var prop = typeof(Actor).GetProperty("StateManager");
            prop?.SetValue(actor, stateManager);
        }
    }

    [Fact]
    public async Task ReceiveAsync_StatusQuery_ReturnsPermissionLevel()
    {
        _stateManager.TryGetStateAsync<PermissionLevel>(StateKeys.HumanPermission, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<PermissionLevel>(true, PermissionLevel.Operator));

        var message = CreateMessage(type: MessageType.StatusQuery);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Type.Should().Be(MessageType.StatusQuery);
        result.From.Should().Be(new Address("human", "test-human"));
        result.To.Should().Be(new Address("agent", "test-sender"));

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Permission").GetString().Should().Be("Operator");
    }

    [Fact]
    public async Task ReceiveAsync_HealthCheck_ReturnsHealthy()
    {
        var message = CreateMessage(type: MessageType.HealthCheck);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Type.Should().Be(MessageType.HealthCheck);
        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Healthy").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageAsOwner_ReturnsAck()
    {
        _stateManager.TryGetStateAsync<PermissionLevel>(StateKeys.HumanPermission, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<PermissionLevel>(true, PermissionLevel.Owner));

        var message = CreateMessage(type: MessageType.Domain);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Acknowledged").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageAsOperator_ReturnsAck()
    {
        _stateManager.TryGetStateAsync<PermissionLevel>(StateKeys.HumanPermission, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<PermissionLevel>(true, PermissionLevel.Operator));

        var message = CreateMessage(type: MessageType.Domain);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Acknowledged").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageAsViewer_ReturnsError()
    {
        // Default is Viewer (no state stored), so no override needed.
        var message = CreateMessage(type: MessageType.Domain);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Error").GetString().Should().Contain("Viewers cannot receive domain messages");
    }

    [Fact]
    public async Task PermissionLevel_RoundTrips_ThroughState()
    {
        // Set permission to Owner.
        await _actor.SetPermissionAsync(PermissionLevel.Owner, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.HumanPermission,
            PermissionLevel.Owner,
            Arg.Any<CancellationToken>());

        // Simulate the state manager returning the stored value.
        _stateManager.TryGetStateAsync<PermissionLevel>(StateKeys.HumanPermission, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<PermissionLevel>(true, PermissionLevel.Owner));

        var permission = await _actor.GetPermissionAsync(TestContext.Current.CancellationToken);
        permission.Should().Be(PermissionLevel.Owner);
    }

    [Fact]
    public async Task GetPermissionAsync_NoState_ReturnsViewer()
    {
        var permission = await _actor.GetPermissionAsync(TestContext.Current.CancellationToken);

        permission.Should().Be(PermissionLevel.Viewer);
    }
}
