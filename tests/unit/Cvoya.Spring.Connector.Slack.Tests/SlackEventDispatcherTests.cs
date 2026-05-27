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
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

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

    private sealed class TestHarness
    {
        public SlackEventDispatcher Dispatcher { get; }
        public ISlackWebApiClient WebApi { get; }
        public ISlackThreadMapStore ThreadMap { get; }
        public RecordingAuditLog AuditLog { get; }
        public IMessageRouter MessageRouter { get; }
        public ITenantConnectorBindingStore BindingStore { get; }
        public IServiceProvider Provider { get; }

        private readonly bool _singleUserMode;

        private TestHarness(
            SlackEventDispatcher dispatcher,
            ISlackWebApiClient webApi,
            ISlackThreadMapStore threadMap,
            RecordingAuditLog auditLog,
            IMessageRouter messageRouter,
            ITenantConnectorBindingStore bindingStore,
            IServiceProvider provider,
            bool singleUserMode)
        {
            Dispatcher = dispatcher;
            WebApi = webApi;
            ThreadMap = threadMap;
            AuditLog = auditLog;
            MessageRouter = messageRouter;
            BindingStore = bindingStore;
            Provider = provider;
            _singleUserMode = singleUserMode;
        }

        public static TestHarness Create(bool singleUserMode)
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

            var messageRouter = Substitute.For<IMessageRouter>();
            messageRouter.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
                .Returns(Cvoya.Spring.Core.Result<Message?, RoutingError>.Success(null!));
            services.AddSingleton(messageRouter);

            services.AddSingleton<IUnboundUserRefusalGate, InMemoryUnboundUserRefusalGate>();

            var provider = services.BuildServiceProvider();

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
                singleUserMode);
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
