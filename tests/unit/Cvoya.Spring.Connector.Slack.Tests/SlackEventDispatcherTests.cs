// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.Slack;
using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connector.Slack.Inbound;
using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Connector.Slack.WebApi;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Routing;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for <see cref="SlackEventDispatcher"/>. Pins the
/// inbound branches per ADR-0061 §2.2 / §2.4 / §3 + ADR-0062 §3.
/// </summary>
public class SlackEventDispatcherTests
{
    private static readonly Guid TestTenantId = new("aaaaaaaa-bbbb-cccc-dddd-000000000001");
    private static readonly Guid OperatorTenantUserId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PrimaryHumanId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task DispatchAsync_MemberJoinedChannel_AutoLeave_WhenSingleUserMode()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create(singleUserMode: true);
        await harness.SeedBindingAsync(ct);

        var envelope = ParseEvent("""
            {
              "team_id": "T-acme",
              "event": {
                "type": "member_joined_channel",
                "user": "U-bot",
                "channel": "C-channel-1"
              }
            }
            """);

        var outcome = await harness.Dispatcher.DispatchAsync(envelope, ct);

        outcome.ShouldBe(SlackEventDispatchOutcome.Handled);
        await harness.WebApi.Received(1).ConversationsLeaveAsync(
            "xoxb-test-token", "C-channel-1", Arg.Any<CancellationToken>());
        await harness.WebApi.Received().PostMessageAsync(
            "xoxb-test-token",
            "C-channel-1",
            SlackEventDispatcher.AutoLeaveMessageText,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        harness.AuditLog.Records.ShouldContain(r =>
            r.Disposition == "auto-leave" && r.EventType == "member_joined_channel");
    }

    [Fact]
    public async Task DispatchAsync_MemberJoinedChannel_NoLeave_WhenSingleUserModeFalse()
    {
        // ADR-0061 §7.3: the auto-leave is gated on single_user_mode.
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create(singleUserMode: false);
        await harness.SeedBindingAsync(ct);

        var envelope = ParseEvent("""
            {
              "team_id": "T-acme",
              "event": {
                "type": "member_joined_channel",
                "user": "U-bot",
                "channel": "C-channel-1"
              }
            }
            """);

        var outcome = await harness.Dispatcher.DispatchAsync(envelope, ct);

        outcome.ShouldBe(SlackEventDispatchOutcome.Ignored);
        await harness.WebApi.DidNotReceive().ConversationsLeaveAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_BoundUserDmMessage_NoThreadTs_DropsWithAudit()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create(singleUserMode: true);
        await harness.SeedBindingAsync(ct);

        var envelope = ParseEvent("""
            {
              "team_id": "T-acme",
              "event": {
                "type": "message",
                "channel_type": "im",
                "user": "U-installer",
                "channel": "D-1",
                "text": "hello"
              }
            }
            """);

        var outcome = await harness.Dispatcher.DispatchAsync(envelope, ct);

        outcome.ShouldBe(SlackEventDispatchOutcome.Handled);
        harness.AuditLog.Records.ShouldContain(r =>
            r.Disposition == "dropped:no-thread" && r.EventType == "message.im");
        await harness.MessageRouter.DidNotReceive().RouteAsync(
            Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_UnboundUserDmMessage_RefusesOncePerSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create(singleUserMode: true);
        await harness.SeedBindingAsync(ct);

        var envelope = ParseEvent("""
            {
              "team_id": "T-acme",
              "event": {
                "type": "message",
                "channel_type": "im",
                "user": "U-stranger",
                "channel": "D-stranger",
                "text": "hello bot"
              }
            }
            """);

        // First call: refusal posted, audit recorded.
        await harness.Dispatcher.DispatchAsync(envelope, ct);
        // Second call: gated — no second refusal.
        await harness.Dispatcher.DispatchAsync(envelope, ct);

        await harness.WebApi.Received(1).PostMessageAsync(
            "xoxb-test-token",
            "D-stranger",
            SlackEventDispatcher.UnboundUserRefusalText,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        harness.AuditLog.Records.Where(r => r.Disposition == "refused:unbound").Count().ShouldBe(1);
        harness.AuditLog.Records.Where(r => r.Disposition == "ignored:unbound-already-refused").Count().ShouldBe(1);
    }

    [Fact]
    public async Task DispatchAsync_BoundUserReply_ForwardsViaRouterWithHumanFrom()
    {
        // Two-party thread (human:op + agent:A) — sender is the human,
        // so fan-out should hit the single non-sender participant once.
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create(singleUserMode: true);
        await harness.SeedBindingAsync(ct);

        // Seed a thread-map row so the inbound reverse-lookup
        // resolves to the SV thread.
        var svThreadId = new Guid("33333333-3333-3333-3333-333333333333");
        await harness.ThreadMap.RecordAsync(
            svThreadId,
            OperatorTenantUserId,
            "T-acme",
            "D-installer",
            "1700.111",
            ct);

        // Seed the thread registry so the dispatcher can read participants.
        await harness.SeedThreadAsync(svThreadId, ct);

        var envelope = ParseEvent("""
            {
              "team_id": "T-acme",
              "event": {
                "type": "message",
                "channel_type": "im",
                "user": "U-installer",
                "channel": "D-installer",
                "thread_ts": "1700.111",
                "text": "hi back"
              }
            }
            """);

        var outcome = await harness.Dispatcher.DispatchAsync(envelope, ct);

        outcome.ShouldBe(SlackEventDispatchOutcome.Handled);
        await harness.MessageRouter.Received(1).RouteAsync(
            Arg.Is<Message>(m =>
                m.From.Scheme == Address.HumanScheme
                && m.From.Id == PrimaryHumanId
                && m.ThreadId == svThreadId.ToString("N")),
            Arg.Any<CancellationToken>());
        harness.AuditLog.Records.ShouldContain(r =>
            r.Disposition == "forwarded" && r.EventType == "message.im");
        // ADR-0060 §2: exactly one audit row per inbound, regardless
        // of fan-out cardinality.
        harness.AuditLog.Records
            .Count(r => r.EventType == "message.im" && r.Disposition == "forwarded")
            .ShouldBe(1);
    }

    [Fact]
    public async Task DispatchAsync_BoundUserReply_MultiPartyThread_FansOutToEveryNonSender()
    {
        // Multi-party thread: human:op (sender) + agent:A + agent:B.
        // The connector must dispatch ONE Message per non-sender
        // participant (#2885); IMessageRouter does not fan out for
        // direct recipient addresses.
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create(singleUserMode: true);
        await harness.SeedBindingAsync(ct);

        var svThreadId = new Guid("55555555-5555-5555-5555-555555555555");
        await harness.ThreadMap.RecordAsync(
            svThreadId,
            OperatorTenantUserId,
            "T-acme",
            "D-installer",
            "1700.222",
            ct);

        var agentA = new Address(Address.AgentScheme, new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var agentB = new Address(Address.AgentScheme, new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        harness.SeedThreadWithParticipants(svThreadId, new[]
        {
            new Address(Address.HumanScheme, PrimaryHumanId),
            agentA,
            agentB,
        });

        var envelope = ParseEvent("""
            {
              "team_id": "T-acme",
              "event": {
                "type": "message",
                "channel_type": "im",
                "user": "U-installer",
                "channel": "D-installer",
                "thread_ts": "1700.222",
                "text": "hello team"
              }
            }
            """);

        var routedMessages = new List<Message>();
        harness.MessageRouter
            .RouteAsync(Arg.Do<Message>(m => routedMessages.Add(m)), Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Core.Result<Message?, RoutingError>.Success(null!));

        var outcome = await harness.Dispatcher.DispatchAsync(envelope, ct);

        outcome.ShouldBe(SlackEventDispatchOutcome.Handled);

        // Exactly two route calls — one per non-sender agent.
        await harness.MessageRouter.Received(2).RouteAsync(
            Arg.Any<Message>(), Arg.Any<CancellationToken>());
        routedMessages.Count.ShouldBe(2);

        // Each routed Message has the same From, ThreadId, Timestamp,
        // and Payload — they differ only by Id and To.
        var threadIdString = svThreadId.ToString("N");
        routedMessages.ShouldAllBe(m =>
            m.From.Scheme == Address.HumanScheme
            && m.From.Id == PrimaryHumanId
            && m.ThreadId == threadIdString);
        routedMessages.Select(m => m.Timestamp).Distinct().Count().ShouldBe(1);
        routedMessages.Select(m => m.Id).Distinct().Count().ShouldBe(2);

        // To-addresses cover the two agents (order-independent).
        var toIds = routedMessages.Select(m => m.To.Id).ToHashSet();
        toIds.ShouldContain(agentA.Id);
        toIds.ShouldContain(agentB.Id);
        routedMessages.ShouldAllBe(m => m.To.Scheme == Address.AgentScheme);

        // One audit row per inbound, not one per recipient.
        harness.AuditLog.Records
            .Count(r => r.EventType == "message.im")
            .ShouldBe(1);
        harness.AuditLog.Records.ShouldContain(r =>
            r.Disposition == "forwarded"
            && r.EventType == "message.im"
            && r.Detail == threadIdString);
    }

    [Fact]
    public async Task DispatchAsync_BoundUserReply_NoNonSenderParticipants_DropsWithAudit()
    {
        // Degenerate registry shape: only the sender's address is in
        // the participant set. The connector must not echo back to
        // self — it drops with audit and zero route calls.
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create(singleUserMode: true);
        await harness.SeedBindingAsync(ct);

        var svThreadId = new Guid("66666666-6666-6666-6666-666666666666");
        await harness.ThreadMap.RecordAsync(
            svThreadId,
            OperatorTenantUserId,
            "T-acme",
            "D-installer",
            "1700.333",
            ct);

        harness.SeedThreadWithParticipants(svThreadId, new[]
        {
            new Address(Address.HumanScheme, PrimaryHumanId),
        });

        var envelope = ParseEvent("""
            {
              "team_id": "T-acme",
              "event": {
                "type": "message",
                "channel_type": "im",
                "user": "U-installer",
                "channel": "D-installer",
                "thread_ts": "1700.333",
                "text": "alone here"
              }
            }
            """);

        var outcome = await harness.Dispatcher.DispatchAsync(envelope, ct);

        outcome.ShouldBe(SlackEventDispatchOutcome.Handled);
        await harness.MessageRouter.DidNotReceive().RouteAsync(
            Arg.Any<Message>(), Arg.Any<CancellationToken>());
        harness.AuditLog.Records.ShouldContain(r =>
            r.Disposition == "dropped:no-recipients"
            && r.EventType == "message.im"
            && r.Detail == svThreadId.ToString("N"));
    }

    [Fact]
    public async Task DispatchAsync_UnknownTeamId_ReturnsUnknownTeam()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create(singleUserMode: true);
        // No binding seeded.

        var envelope = ParseEvent("""
            {
              "team_id": "T-other",
              "event": {
                "type": "message",
                "channel_type": "im",
                "user": "U-installer",
                "channel": "D-1"
              }
            }
            """);

        var outcome = await harness.Dispatcher.DispatchAsync(envelope, ct);
        outcome.ShouldBe(SlackEventDispatchOutcome.UnknownTeam);
    }

    private static JsonElement ParseEvent(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public async Task DispatchAsync_BoundUserReply_MultiParty_FannedOutMessagesReachAgentMailboxes()
    {
        // #2901 — the per-participant fan-out (#2885) is unit-tested at the
        // IMessageRouter boundary; this composes it with a REAL MessageRouter
        // so the fanned-out messages are actually DELIVERED to each agent's
        // mailbox (proxy.ReceiveAsync): inbound Slack event → fan-out →
        // routing → two mailboxes, end to end.
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create(singleUserMode: true, useRealRouter: true);
        await harness.SeedBindingAsync(ct);

        var svThreadId = new Guid("77777777-7777-7777-7777-777777777777");
        await harness.ThreadMap.RecordAsync(
            svThreadId, OperatorTenantUserId, "T-acme", "D-installer", "1700.333", ct);

        var agentA = new Address(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-0000000000a1"));
        var agentB = new Address(Address.AgentScheme, new Guid("bbbbbbbb-0000-0000-0000-0000000000b1"));
        harness.SeedThreadWithParticipants(svThreadId, new[]
        {
            new Address(Address.HumanScheme, PrimaryHumanId),
            agentA,
            agentB,
        });

        // Wire the directory + agent proxies so the real router resolves each
        // agent address to a mailbox (substitute IAgent.ReceiveAsync).
        Message? toA = null;
        Message? toB = null;
        var mailboxA = Substitute.For<IAgent>();
        var mailboxB = Substitute.For<IAgent>();
        mailboxA.ReceiveAsync(Arg.Do<Message>(m => toA = m), Arg.Any<CancellationToken>()).Returns((Message?)null);
        mailboxB.ReceiveAsync(Arg.Do<Message>(m => toB = m), Arg.Any<CancellationToken>()).Returns((Message?)null);
        WireAgentMailbox(harness, agentA, mailboxA);
        WireAgentMailbox(harness, agentB, mailboxB);

        var envelope = ParseEvent("""
            {
              "team_id": "T-acme",
              "event": {
                "type": "message",
                "channel_type": "im",
                "user": "U-installer",
                "channel": "D-installer",
                "thread_ts": "1700.333",
                "text": "hello team"
              }
            }
            """);

        var outcome = await harness.Dispatcher.DispatchAsync(envelope, ct);

        outcome.ShouldBe(SlackEventDispatchOutcome.Handled);

        // Both agent mailboxes received the message — the fan-out reached each
        // mailbox THROUGH the real router, not just the router boundary.
        await mailboxA.Received(1).ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
        await mailboxB.Received(1).ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
        toA.ShouldNotBeNull();
        toB.ShouldNotBeNull();

        // Same logical inbound: shared From (the operator's Hat) + ThreadId,
        // distinct message ids (#2885).
        var threadIdString = svThreadId.ToString("N");
        toA!.From.Scheme.ShouldBe(Address.HumanScheme);
        toA.From.Id.ShouldBe(PrimaryHumanId);
        toA.ThreadId.ShouldBe(threadIdString);
        toB!.ThreadId.ShouldBe(threadIdString);
        toA.Id.ShouldNotBe(toB.Id);

        // One audit row per inbound, not one per recipient.
        harness.AuditLog.Records.Count(r => r.EventType == "message.im").ShouldBe(1);
    }

    private static void WireAgentMailbox(TestHarness harness, Address agent, IAgent mailbox)
    {
        harness.DirectoryService!
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == Address.AgentScheme && a.Id == agent.Id),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(agent, agent.Id, "Agent", "test", null, DateTimeOffset.UtcNow));
        harness.AgentProxyResolver!
            .Resolve(
                Arg.Is<string>(s => string.Equals(s, Address.AgentScheme, StringComparison.OrdinalIgnoreCase)),
                agent.Id.ToString("N"))
            .Returns(mailbox);
    }

    private sealed class NoOpMessageWriter : Cvoya.Spring.Dapr.Threads.IMessageWriter
    {
        public Task WriteAsync(Message message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestHarness
    {
        public SlackEventDispatcher Dispatcher { get; }
        public ISlackWebApiClient WebApi { get; }
        public ISlackThreadMapStore ThreadMap { get; }
        public RecordingAuditLog AuditLog { get; }
        public IMessageRouter MessageRouter { get; }
        public ITenantConnectorBindingStore BindingStore { get; }
        public IServiceProvider Provider { get; }

        // Set only when the harness is built with a real MessageRouter
        // (#2901); the test wires per-agent directory entries + proxies.
        public IDirectoryService? DirectoryService { get; }
        public IAgentProxyResolver? AgentProxyResolver { get; }

        private readonly bool _singleUserMode;

        private TestHarness(
            SlackEventDispatcher dispatcher,
            ISlackWebApiClient webApi,
            ISlackThreadMapStore threadMap,
            RecordingAuditLog auditLog,
            IMessageRouter messageRouter,
            ITenantConnectorBindingStore bindingStore,
            IServiceProvider provider,
            bool singleUserMode,
            IDirectoryService? directoryService = null,
            IAgentProxyResolver? agentProxyResolver = null)
        {
            Dispatcher = dispatcher;
            WebApi = webApi;
            ThreadMap = threadMap;
            AuditLog = auditLog;
            MessageRouter = messageRouter;
            BindingStore = bindingStore;
            Provider = provider;
            _singleUserMode = singleUserMode;
            DirectoryService = directoryService;
            AgentProxyResolver = agentProxyResolver;
        }

        public static TestHarness Create(bool singleUserMode, bool useRealRouter = false)
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var tenantContext = Substitute.For<ITenantContext>();
            tenantContext.CurrentTenantId.Returns(TestTenantId);
            services.AddSingleton(tenantContext);

            var hatResolver = Substitute.For<ITenantUserHumanResolver>();
            hatResolver.PickFromAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
                .Returns(new Address(Address.HumanScheme, PrimaryHumanId));
            services.AddSingleton(hatResolver);

            var threadRegistry = Substitute.For<IThreadRegistry>();
            services.AddSingleton(threadRegistry);

            // Use in-memory EF for the thread-map + binding store.
            // CRITICAL: the database NAME must be computed once and
            // captured — if computed inside the lambda body each
            // DbContext instantiation yields a different database.
            var dbName = $"EventDispatcherTests_{Guid.NewGuid():N}";
            services.AddDbContext<Cvoya.Spring.Dapr.Data.SpringDbContext>(o =>
                o.UseInMemoryDatabase(dbName));
            services.AddScoped<Cvoya.Spring.Dapr.Data.IUnitConnectorBindingRepository, Cvoya.Spring.Dapr.Data.UnitConnectorBindingRepository>();
            services.AddScoped<Cvoya.Spring.Dapr.Data.ITenantConnectorBindingRepository, Cvoya.Spring.Dapr.Data.TenantConnectorBindingRepository>();
            services.AddSingleton<ITenantConnectorBindingStore, Cvoya.Spring.Dapr.Connectors.TenantConnectorBindingStore>();
            services.AddSingleton<ISlackThreadMapStore, Cvoya.Spring.Dapr.Connectors.Slack.EfSlackThreadMapStore>();

            services.TryAddEnumerable(ServiceDescriptor.Singleton<ITenantBoundUserExtractor, SlackBoundUserExtractor>());

            // Fake secret resolver returning the bot token verbatim.
            var resolver = Substitute.For<ISecretResolver>();
            resolver.ResolveWithPathAsync(Arg.Is<SecretRef>(r => r.Name.Contains("bot-token", StringComparison.Ordinal)), Arg.Any<CancellationToken>())
                .Returns(new SecretResolution(
                    Value: "xoxb-test-token",
                    Path: SecretResolvePath.Direct,
                    EffectiveRef: new SecretRef(SecretScope.Tenant, TestTenantId, "slack/T-acme/bot-token"),
                    Version: 1));
            services.AddSingleton(resolver);

            var webApi = Substitute.For<ISlackWebApiClient>();
            webApi.PostMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new SlackPostMessageResult(Ok: true, Error: null, ChannelId: "C-1", MessageTs: "1700.000"));
            webApi.ConversationsLeaveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new SlackResult(Ok: true, Error: null));
            services.AddSingleton(webApi);

            var auditLog = new RecordingAuditLog();
            services.AddSingleton<ISlackInboundAuditLog>(auditLog);

            IMessageRouter messageRouter = Substitute.For<IMessageRouter>();
            messageRouter.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
                .Returns(Cvoya.Spring.Core.Result<Message?, RoutingError>.Success(null!));

            // #2901 — when useRealRouter, swap the substitute for a REAL
            // MessageRouter so the dispatcher's fanned-out messages are
            // actually delivered to agent mailboxes (proxy.ReceiveAsync). The
            // directory + agent proxies are exposed for the test to wire per
            // recipient; the router persists through a no-op writer.
            IDirectoryService? directoryService = null;
            IAgentProxyResolver? agentProxyResolver = null;
            if (useRealRouter)
            {
                directoryService = Substitute.For<IDirectoryService>();
                agentProxyResolver = Substitute.For<IAgentProxyResolver>();
                services.AddScoped<Cvoya.Spring.Dapr.Threads.IMessageWriter>(_ => new NoOpMessageWriter());
            }
            else
            {
                services.AddSingleton(messageRouter);
            }

            services.AddSingleton<IUnboundUserRefusalGate, InMemoryUnboundUserRefusalGate>();

            var provider = services.BuildServiceProvider();

            if (useRealRouter)
            {
                messageRouter = new Cvoya.Spring.Dapr.Routing.MessageRouter(
                    directoryService!,
                    agentProxyResolver!,
                    Substitute.For<Cvoya.Spring.Dapr.Auth.IPermissionService>(),
                    NullLoggerFactory.Instance,
                    provider.GetRequiredService<IServiceScopeFactory>());
            }

            var dispatcher = new SlackEventDispatcher(
                provider.GetRequiredService<IServiceScopeFactory>(),
                webApi,
                provider.GetRequiredService<ISlackThreadMapStore>(),
                provider.GetRequiredService<IUnboundUserRefusalGate>(),
                auditLog,
                messageRouter,
                NullLoggerFactory.Instance);

            return new TestHarness(
                dispatcher,
                webApi,
                provider.GetRequiredService<ISlackThreadMapStore>(),
                auditLog,
                messageRouter,
                provider.GetRequiredService<ITenantConnectorBindingStore>(),
                provider,
                singleUserMode,
                directoryService,
                agentProxyResolver);
        }

        public async Task SeedBindingAsync(CancellationToken ct)
        {
            var config = new TenantSlackConfig(
                TeamId: "T-acme",
                TeamName: "Acme",
                BotUserId: "U-bot",
                BotTokenSecretName: "slack/T-acme/bot-token",
                SigningSecretSecretName: "slack/T-acme/signing-secret",
                InstallerUserId: "U-installer",
                SingleUserMode: _singleUserMode,
                Mode: SlackBindingMode.Workspace,
                BoundUsers: new[]
                {
                    new TenantSlackBoundUser("U-installer", OperatorTenantUserId),
                });
            var configJson = JsonSerializer.SerializeToElement(config);
            await BindingStore.SetAsync(SlackInstallStore.ConnectorSlug, SlackConnectorType.SlackTypeId, configJson, "T-acme", ct);
        }

        public async Task SeedThreadAsync(Guid svThreadId, CancellationToken ct)
        {
            var threadRegistry = Provider.GetRequiredService<IThreadRegistry>();
            var hatAddress = new Address(Address.HumanScheme, PrimaryHumanId);
            var agentAddress = new Address(Address.AgentScheme, new Guid("44444444-4444-4444-4444-444444444444"));
            threadRegistry.ResolveAsync(svThreadId.ToString("N"), Arg.Any<CancellationToken>())
                .Returns(new ThreadRegistryEntry(
                    ThreadId: svThreadId.ToString("N"),
                    Participants: new[] { hatAddress, agentAddress },
                    CreatedAt: DateTimeOffset.UtcNow));
            await Task.CompletedTask;
        }

        public void SeedThreadWithParticipants(Guid svThreadId, IReadOnlyList<Address> participants)
        {
            var threadRegistry = Provider.GetRequiredService<IThreadRegistry>();
            threadRegistry.ResolveAsync(svThreadId.ToString("N"), Arg.Any<CancellationToken>())
                .Returns(new ThreadRegistryEntry(
                    ThreadId: svThreadId.ToString("N"),
                    Participants: participants,
                    CreatedAt: DateTimeOffset.UtcNow));
        }
    }

    private sealed class RecordingAuditLog : ISlackInboundAuditLog
    {
        public List<SlackInboundAuditEvent> Records { get; } = new();

        public Task RecordAsync(SlackInboundAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Records.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
