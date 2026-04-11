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
/// Unit tests for <see cref="ConnectorActor"/> covering message routing,
/// status queries, health checks, domain handling, and connection status management.
/// </summary>
public class ConnectorActorTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly ConnectorActor _actor;

    public ConnectorActorTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var host = ActorHost.CreateForTest<ConnectorActor>(new ActorTestOptions
        {
            ActorId = new ActorId("test-connector")
        });
        _actor = new ConnectorActor(host, _loggerFactory);
        SetStateManager(_actor, _stateManager);

        // Default: no state stored.
        _stateManager.TryGetStateAsync<ConnectionStatus>(StateKeys.ConnectorStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConnectionStatus>(false, default));
        _stateManager.TryGetStateAsync<string>(StateKeys.ConnectorType, Arg.Any<CancellationToken>())
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
            new Address("connector", "test-connector"),
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
    public async Task ReceiveAsync_StatusQuery_ReturnsConnectionStatus()
    {
        _stateManager.TryGetStateAsync<ConnectionStatus>(StateKeys.ConnectorStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConnectionStatus>(true, ConnectionStatus.Connected));

        var message = CreateMessage(type: MessageType.StatusQuery);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Type.Should().Be(MessageType.StatusQuery);
        result.From.Should().Be(new Address("connector", "test-connector"));
        result.To.Should().Be(new Address("agent", "test-sender"));

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Status").GetString().Should().Be("Connected");
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
    public async Task ReceiveAsync_DomainMessage_ReturnsAck()
    {
        var message = CreateMessage(type: MessageType.Domain);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Acknowledged").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ConnectionStatus_RoundTrips_ThroughState()
    {
        // Set status to Connected.
        await _actor.SetConnectionStatusAsync(ConnectionStatus.Connected, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.ConnectorStatus,
            ConnectionStatus.Connected,
            Arg.Any<CancellationToken>());

        // Simulate the state manager returning the stored value.
        _stateManager.TryGetStateAsync<ConnectionStatus>(StateKeys.ConnectorStatus, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ConnectionStatus>(true, ConnectionStatus.Connected));

        var status = await _actor.GetConnectionStatusAsync(TestContext.Current.CancellationToken);
        status.Should().Be(ConnectionStatus.Connected);
    }

    [Fact]
    public async Task GetConnectionStatusAsync_NoState_ReturnsDisconnected()
    {
        var status = await _actor.GetConnectionStatusAsync(TestContext.Current.CancellationToken);

        status.Should().Be(ConnectionStatus.Disconnected);
    }
}