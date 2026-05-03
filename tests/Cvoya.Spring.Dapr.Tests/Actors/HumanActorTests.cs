// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="HumanActor"/> covering message routing,
/// status queries, health checks, permission enforcement, and state management.
/// </summary>
public class HumanActorTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly HumanActor _actor;

    public HumanActorTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activityEventBus.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var host = ActorHost.CreateForTest<HumanActor>(new ActorTestOptions
        {
            ActorId = new ActorId(TestSlugIds.HexFor("test-human"))
        });
        _actor = new HumanActor(host, _activityEventBus, _loggerFactory);
        SetStateManager(_actor, _stateManager);

        // Default: no state stored (HumanActor.GetPermissionAsync now
        // defaults to Operator — see #1479 / #1473).
        _stateManager.TryGetStateAsync<PermissionLevel>(StateKeys.HumanPermission, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<PermissionLevel>(false, default));
        _stateManager.TryGetStateAsync<string>(StateKeys.HumanIdentity, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<string>(false, default!));
    }

    private static Message CreateMessage(
        MessageType type = MessageType.Domain,
        string? threadId = null,
        JsonElement? payload = null)
    {
        return new Message(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("test-sender")),
            Address.For("human", TestSlugIds.HexFor("test-human")),
            type,
            threadId ?? Guid.NewGuid().ToString(),
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

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.StatusQuery);
        result.From.ShouldBe(Address.For("human", TestSlugIds.HexFor("test-human")));
        result.To.ShouldBe(Address.For("agent", TestSlugIds.HexFor("test-sender")));

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Permission").GetString().ShouldBe("Operator");
    }

    [Fact]
    public async Task ReceiveAsync_HealthCheck_ReturnsHealthy()
    {
        var message = CreateMessage(type: MessageType.HealthCheck);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.HealthCheck);
        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Healthy").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageAsOwner_ReturnsAck()
    {
        _stateManager.TryGetStateAsync<PermissionLevel>(StateKeys.HumanPermission, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<PermissionLevel>(true, PermissionLevel.Owner));

        var message = CreateMessage(type: MessageType.Domain);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Acknowledged").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageAsOperator_ReturnsAck()
    {
        _stateManager.TryGetStateAsync<PermissionLevel>(StateKeys.HumanPermission, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<PermissionLevel>(true, PermissionLevel.Operator));

        var message = CreateMessage(type: MessageType.Domain);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Acknowledged").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageAsViewer_ReturnsError()
    {
        // The default is now Operator (#1479 interim) — explicitly set
        // Viewer here so this test still exercises the rejection path.
        _stateManager.TryGetStateAsync<PermissionLevel>(StateKeys.HumanPermission, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<PermissionLevel>(true, PermissionLevel.Viewer));
        var message = CreateMessage(type: MessageType.Domain);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Error").GetString()!.ShouldContain("Viewers cannot receive domain messages");
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
        permission.ShouldBe(PermissionLevel.Owner);
    }

    [Fact]
    public async Task GetPermissionAsync_NoState_ReturnsOperator()
    {
        // #1479 interim: HumanActor defaults to Operator (not Viewer) so the
        // OSS new-conversation round-trip works without a separate promotion
        // step. Long-term shape (owner-by-creation + thread membership) is
        // tracked in #1479; this test will change again when that lands.
        var permission = await _actor.GetPermissionAsync(TestContext.Current.CancellationToken);

        permission.ShouldBe(PermissionLevel.Operator);
    }

    // --- Unit-Scoped Permission Tests ---

    [Fact]
    public async Task SetPermissionForUnitAsync_StoresUnitPermission()
    {
        _stateManager.TryGetStateAsync<Dictionary<string, PermissionLevel>>(
            StateKeys.HumanUnitPermissions, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, PermissionLevel>>(false, default!));

        await _actor.SetPermissionForUnitAsync("unit-1", PermissionLevel.Operator, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.HumanUnitPermissions,
            Arg.Is<Dictionary<string, PermissionLevel>>(d =>
                d.ContainsKey("unit-1") && d["unit-1"] == PermissionLevel.Operator),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPermissionForUnitAsync_ExistingUnit_ReturnsPermission()
    {
        var unitPermissions = new Dictionary<string, PermissionLevel>
        {
            ["unit-1"] = PermissionLevel.Owner
        };
        _stateManager.TryGetStateAsync<Dictionary<string, PermissionLevel>>(
            StateKeys.HumanUnitPermissions, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, PermissionLevel>>(true, unitPermissions));

        var result = await _actor.GetPermissionForUnitAsync("unit-1", TestContext.Current.CancellationToken);

        result.ShouldBe(PermissionLevel.Owner);
    }

    [Fact]
    public async Task GetPermissionForUnitAsync_NonExistentUnit_ReturnsNull()
    {
        _stateManager.TryGetStateAsync<Dictionary<string, PermissionLevel>>(
            StateKeys.HumanUnitPermissions, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, PermissionLevel>>(false, default!));

        var result = await _actor.GetPermissionForUnitAsync("unknown-unit", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task RemovePermissionForUnitAsync_ExistingUnit_DropsEntry()
    {
        // #454 adds RemovePermissionForUnitAsync to keep the human-side
        // view consistent with the unit-side after DELETE /humans/{humanId}.
        var unitPermissions = new Dictionary<string, PermissionLevel>
        {
            ["unit-1"] = PermissionLevel.Owner,
            ["unit-2"] = PermissionLevel.Viewer,
        };
        _stateManager.TryGetStateAsync<Dictionary<string, PermissionLevel>>(
            StateKeys.HumanUnitPermissions, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, PermissionLevel>>(true, unitPermissions));

        await _actor.RemovePermissionForUnitAsync("unit-1", TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.HumanUnitPermissions,
            Arg.Is<Dictionary<string, PermissionLevel>>(d =>
                !d.ContainsKey("unit-1") && d.ContainsKey("unit-2")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovePermissionForUnitAsync_UnknownUnit_IsNoOp()
    {
        _stateManager.TryGetStateAsync<Dictionary<string, PermissionLevel>>(
            StateKeys.HumanUnitPermissions, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, PermissionLevel>>(false, default!));

        await _actor.RemovePermissionForUnitAsync("unknown-unit", TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.HumanUnitPermissions,
            Arg.Any<Dictionary<string, PermissionLevel>>(),
            Arg.Any<CancellationToken>());
    }

    // --- Per-thread last-read-at cursor tests (#1477) ---

    [Fact]
    public async Task MarkReadAsync_NewThread_StoresCursor()
    {
        _stateManager.TryGetStateAsync<Dictionary<string, DateTimeOffset>>(
            StateKeys.HumanLastReadAt, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, DateTimeOffset>>(false, default!));

        var readAt = DateTimeOffset.UtcNow;
        await _actor.MarkReadAsync("thread-1", readAt, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.HumanLastReadAt,
            Arg.Is<Dictionary<string, DateTimeOffset>>(d =>
                d.ContainsKey("thread-1") && d["thread-1"] == readAt),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkReadAsync_AdvancingCursor_UpdatesStoredValue()
    {
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
        var later = DateTimeOffset.UtcNow;

        _stateManager.TryGetStateAsync<Dictionary<string, DateTimeOffset>>(
            StateKeys.HumanLastReadAt, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, DateTimeOffset>>(
                true,
                new Dictionary<string, DateTimeOffset> { ["thread-1"] = earlier }));

        await _actor.MarkReadAsync("thread-1", later, TestContext.Current.CancellationToken);

        await _stateManager.Received(1).SetStateAsync(
            StateKeys.HumanLastReadAt,
            Arg.Is<Dictionary<string, DateTimeOffset>>(d =>
                d.ContainsKey("thread-1") && d["thread-1"] == later),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkReadAsync_OlderTimestamp_IsNoOp()
    {
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
        var later = DateTimeOffset.UtcNow;

        _stateManager.TryGetStateAsync<Dictionary<string, DateTimeOffset>>(
            StateKeys.HumanLastReadAt, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, DateTimeOffset>>(
                true,
                new Dictionary<string, DateTimeOffset> { ["thread-1"] = later }));

        // Calling with an older timestamp should not advance the cursor.
        await _actor.MarkReadAsync("thread-1", earlier, TestContext.Current.CancellationToken);

        await _stateManager.DidNotReceive().SetStateAsync(
            StateKeys.HumanLastReadAt,
            Arg.Any<Dictionary<string, DateTimeOffset>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLastReadAtAsync_NoState_ReturnsEmptyArray()
    {
        _stateManager.TryGetStateAsync<Dictionary<string, DateTimeOffset>>(
            StateKeys.HumanLastReadAt, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, DateTimeOffset>>(false, default!));

        var result = await _actor.GetLastReadAtAsync(TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Length.ShouldBe(0);
    }

    [Fact]
    public async Task GetLastReadAtAsync_WithState_RoundTripsMap()
    {
        var ts = DateTimeOffset.UtcNow;
        _stateManager.TryGetStateAsync<Dictionary<string, DateTimeOffset>>(
            StateKeys.HumanLastReadAt, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<Dictionary<string, DateTimeOffset>>(
                true,
                new Dictionary<string, DateTimeOffset>
                {
                    ["thread-a"] = ts,
                    ["thread-b"] = ts.AddMinutes(-3),
                }));

        var result = await _actor.GetLastReadAtAsync(TestContext.Current.CancellationToken);

        result.Length.ShouldBe(2);
        result.ShouldContain(e => e.ThreadId == "thread-a" && e.LastReadAt == ts);
        result.ShouldContain(e => e.ThreadId == "thread-b" && e.LastReadAt == ts.AddMinutes(-3));
    }
}