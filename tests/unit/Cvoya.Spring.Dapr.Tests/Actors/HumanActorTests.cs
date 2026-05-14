// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="HumanActor"/> covering message routing,
/// status queries, health checks, permission enforcement, and EF-backed
/// state management per ADR-0040.
/// </summary>
public class HumanActorTests : IDisposable
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly ServiceProvider _serviceProvider;
    private readonly Guid _humanId = Guid.NewGuid();
    private readonly HumanActor _actor;

    public HumanActorTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activityEventBus.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        var dbName = $"HumanActorTest-{Guid.NewGuid()}";
        services.AddSingleton<ITenantContext>(new StaticTenantContext(OssTenantIds.Default));
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        _serviceProvider = services.BuildServiceProvider();

        var host = ActorHost.CreateForTest<HumanActor>(new ActorTestOptions
        {
            ActorId = new ActorId(GuidFormatter.Format(_humanId))
        });
        _actor = new HumanActor(
            host,
            _activityEventBus,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _loggerFactory);
        SetStateManager(_actor, _stateManager);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<HumanEntity> SeedHumanAsync(
        PermissionLevel? permission = null,
        NotificationPreferences? preferences = null,
        Guid? tenantId = null,
        string username = "test-human")
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var entity = new HumanEntity
        {
            Id = _humanId,
            TenantId = tenantId ?? OssTenantIds.Default,
            Username = username,
            DisplayName = username,
            PermissionLevel = permission ?? PermissionLevel.Operator,
            NotificationPreferences = preferences,
        };
        db.Humans.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return entity;
    }

    private async Task<HumanEntity?> ReadHumanAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        return await db.Humans
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == _humanId, TestContext.Current.CancellationToken);
    }

    private Message CreateMessage(
        MessageType type = MessageType.Domain,
        string? threadId = null,
        JsonElement? payload = null)
    {
        return new Message(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("test-sender")),
            Address.For("human", GuidFormatter.Format(_humanId)),
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
    public async Task ReceiveAsync_StatusQuery_ReturnsPermissionLevelAndIdentity()
    {
        await SeedHumanAsync(permission: PermissionLevel.Operator, username: "ada");

        var message = CreateMessage(type: MessageType.StatusQuery);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.StatusQuery);
        result.From.ShouldBe(Address.For("human", GuidFormatter.Format(_humanId)));
        result.To.ShouldBe(Address.For("agent", TestSlugIds.HexFor("test-sender")));

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Permission").GetString().ShouldBe("Operator");
        // ADR-0040: identity is read from HumanEntity.Username, not actor state.
        payload.GetProperty("Identity").GetString().ShouldBe("ada");
    }

    [Fact]
    public async Task ReceiveAsync_StatusQuery_NoEfRow_ReturnsUnknownIdentity()
    {
        // No HumanEntity row for the actor id — the actor falls back to
        // the OSS interim default (Operator) and an "unknown" identity.
        var message = CreateMessage(type: MessageType.StatusQuery);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Permission").GetString().ShouldBe("Operator");
        payload.GetProperty("Identity").GetString().ShouldBe("unknown");
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
        await SeedHumanAsync(permission: PermissionLevel.Owner);

        var message = CreateMessage(type: MessageType.Domain);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Acknowledged").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageAsOperator_ReturnsAck()
    {
        await SeedHumanAsync(permission: PermissionLevel.Operator);

        var message = CreateMessage(type: MessageType.Domain);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Acknowledged").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageAsViewer_ReturnsError()
    {
        await SeedHumanAsync(permission: PermissionLevel.Viewer);

        var message = CreateMessage(type: MessageType.Domain);

        var result = await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Error").GetString()!.ShouldContain("Viewers cannot receive domain messages");
    }

    [Fact]
    public async Task PermissionLevel_RoundTripsThroughEf()
    {
        // First write — no row exists yet; actor materialises one.
        await _actor.SetPermissionAsync(PermissionLevel.Owner, TestContext.Current.CancellationToken);

        var stored = await ReadHumanAsync();
        stored.ShouldNotBeNull();
        stored!.PermissionLevel.ShouldBe(PermissionLevel.Owner);

        // Read back through the actor — survives across "actor restart"
        // because there is no actor-state copy; every read hits EF.
        var permission = await _actor.GetPermissionAsync(TestContext.Current.CancellationToken);
        permission.ShouldBe(PermissionLevel.Owner);
    }

    [Fact]
    public async Task SetPermissionAsync_UpdatesExistingRow()
    {
        await SeedHumanAsync(permission: PermissionLevel.Viewer, username: "ada");

        await _actor.SetPermissionAsync(PermissionLevel.Owner, TestContext.Current.CancellationToken);

        var stored = await ReadHumanAsync();
        stored.ShouldNotBeNull();
        stored!.PermissionLevel.ShouldBe(PermissionLevel.Owner);
        // Username untouched — only the permission column changed.
        stored.Username.ShouldBe("ada");
    }

    [Fact]
    public async Task SetPermissionAsync_NeverWritesActorState()
    {
        await _actor.SetPermissionAsync(PermissionLevel.Owner, TestContext.Current.CancellationToken);

        // ADR-0040: the actor must not maintain a Human:Permission key.
        await _stateManager.DidNotReceive().SetStateAsync(
            "Human:Permission",
            Arg.Any<PermissionLevel>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPermissionAsync_NoEfRow_ReturnsOperator()
    {
        // #1479 interim: defaulting to Operator unblocks the new-conversation
        // round-trip without a separate promotion step.
        var permission = await _actor.GetPermissionAsync(TestContext.Current.CancellationToken);

        permission.ShouldBe(PermissionLevel.Operator);
    }

    [Fact]
    public async Task NotificationPreferences_RoundTripThroughEf()
    {
        var prefs = new NotificationPreferences(EmailEnabled: true, InAppEnabled: false);

        await _actor.SetNotificationPreferencesAsync(prefs, TestContext.Current.CancellationToken);

        var read = await _actor.GetNotificationPreferencesAsync(TestContext.Current.CancellationToken);
        read.ShouldNotBeNull();
        read!.EmailEnabled.ShouldBeTrue();
        read.InAppEnabled.ShouldBeFalse();

        var stored = await ReadHumanAsync();
        stored!.NotificationPreferences.ShouldNotBeNull();
        stored.NotificationPreferences!.EmailEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetNotificationPreferencesAsync_NoRow_ReturnsNull()
    {
        var prefs = await _actor.GetNotificationPreferencesAsync(TestContext.Current.CancellationToken);
        prefs.ShouldBeNull();
    }

    [Fact]
    public async Task SetNotificationPreferencesAsync_NeverWritesActorState()
    {
        await _actor.SetNotificationPreferencesAsync(
            new NotificationPreferences(true, true),
            TestContext.Current.CancellationToken);

        // ADR-0040: the actor must not maintain a Human:NotificationPreferences key.
        await _stateManager.DidNotReceive().SetStateAsync(
            "Human:NotificationPreferences",
            Arg.Any<NotificationPreferences?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenantIsolation_PermissionSetOnOneTenantNotVisibleToAnother()
    {
        // Seed a row under a different tenant id.
        var otherTenant = Guid.NewGuid();
        await SeedHumanAsync(
            permission: PermissionLevel.Owner,
            tenantId: otherTenant,
            username: "ada");

        // The actor's DbContext is bound to OssTenantIds.Default; the row
        // under the other tenant is filtered out.
        var permission = await _actor.GetPermissionAsync(TestContext.Current.CancellationToken);
        permission.ShouldBe(PermissionLevel.Operator);
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

    [Fact]
    public async Task ReceiveAsync_DomainMessageWithStringPayload_SummaryIsBodyText()
    {
        // #1636: production must NEVER write the legacy "Received Domain
        // message <uuid> from <address>" envelope as the activity-event
        // summary. The summary is the actual message text — the portal renders
        // it directly as a chat bubble line without templating.
        var threadId = "conv-1636-string";
        var payload = JsonSerializer.SerializeToElement("Approve merge?");
        var message = CreateMessage(threadId: threadId, payload: payload);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.MessageReceived
                && e.Summary == "Approve merge?"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessageWithAgentReplyShape_SummaryIsOutputText()
    {
        // #1636 / #1547: agent replies arrive as { Output, ExitCode } objects;
        // the summary is the Output string, never the leaky envelope.
        var threadId = "conv-1636-output";
        var payload = JsonSerializer.SerializeToElement(new
        {
            Output = "Looks good — shipping.",
            ExitCode = 0,
        });
        var message = CreateMessage(threadId: threadId, payload: payload);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.MessageReceived
                && e.Summary == "Looks good — shipping."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_SummaryNeverContainsLegacyEnvelopeTemplate()
    {
        // #1636: hard regression guard — the receive-event summary must never
        // start with "Received " or carry the message GUID / sender address.
        var threadId = "conv-1636-no-envelope";
        var payload = JsonSerializer.SerializeToElement("hello");
        var message = CreateMessage(threadId: threadId, payload: payload);

        await _actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await _activityEventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.MessageReceived
                && !e.Summary.StartsWith("Received ")
                && !e.Summary.Contains(message.Id.ToString())
                && !e.Summary.Contains(message.From.Path)),
            Arg.Any<CancellationToken>());
    }
}
