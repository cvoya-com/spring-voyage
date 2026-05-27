// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.Slack;
using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connector.Slack.Commands;
using Cvoya.Spring.Connector.Slack.Inbound;
using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Connector.Slack.Slug;
using Cvoya.Spring.Connector.Slack.WebApi;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for <see cref="SlackCommandDispatcher"/>. Pins the three
/// slash-command branches (<c>/sv-thread</c>, <c>/sv-threads</c>,
/// <c>/sv-help</c>) and the modal-submit view_submission path per
/// ADR-0061 §5.
/// </summary>
public class SlackCommandDispatcherTests
{
    private static readonly Guid TestTenantId = new("aaaaaaaa-bbbb-cccc-dddd-000000000001");
    private static readonly Guid OperatorTenantUserId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PrimaryHumanId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task DispatchAsync_SvThread_FromDm_OpensModal()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        await harness.SeedBindingAsync(ct);

        var form = BuildForm(SlackCommandDispatcher.SvThreadCommand, channelName: "directmessage");
        var outcome = await harness.Dispatcher.DispatchAsync(form, ct);

        outcome.ShouldBe(SlackCommandDispatchOutcome.Handled);
        await harness.WebApi.Received(1).ViewsOpenAsync(
            "xoxb-test-token",
            "trigger-1",
            Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_SvHelp_FromDm_PostsCheatSheet()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        await harness.SeedBindingAsync(ct);

        var form = BuildForm(SlackCommandDispatcher.SvHelpCommand, channelName: "directmessage", channelId: "D1");
        var outcome = await harness.Dispatcher.DispatchAsync(form, ct);

        outcome.ShouldBe(SlackCommandDispatchOutcome.Handled);
        await harness.WebApi.Received(1).PostMessageAsync(
            "xoxb-test-token",
            "D1",
            SlackCommandDispatcher.CheatSheetText,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_SvThreads_FromDm_OpensListModal()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        await harness.SeedBindingAsync(ct);

        // Seed a couple of thread mappings.
        await harness.ThreadMap.RecordAsync(
            Guid.NewGuid(), OperatorTenantUserId, "T-acme", "D-installer", "1700.1", ct);
        await harness.ThreadMap.RecordAsync(
            Guid.NewGuid(), OperatorTenantUserId, "T-acme", "D-installer", "1700.2", ct);

        var form = BuildForm(SlackCommandDispatcher.SvThreadsCommand, channelName: "directmessage");
        var outcome = await harness.Dispatcher.DispatchAsync(form, ct);

        outcome.ShouldBe(SlackCommandDispatchOutcome.Handled);
        await harness.WebApi.Received(1).ViewsOpenAsync(
            "xoxb-test-token",
            "trigger-1",
            Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_NonDm_AuditsRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        await harness.SeedBindingAsync(ct);

        var form = BuildForm(SlackCommandDispatcher.SvThreadCommand, channelName: "engineering");
        var outcome = await harness.Dispatcher.DispatchAsync(form, ct);

        outcome.ShouldBe(SlackCommandDispatchOutcome.Handled);
        harness.AuditLog.Records.ShouldContain(r =>
            r.Disposition == "refused:non-dm" && r.EventType == "command:" + SlackCommandDispatcher.SvThreadCommand);
        await harness.WebApi.DidNotReceive().ViewsOpenAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchInteractionAsync_ViewSubmission_CreatesThread_AndPostsParent()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        await harness.SeedBindingAsync(ct);

        // Wire IThreadRegistry to return a stable thread id for the
        // selected-participants set.
        var svThreadId = new Guid("33333333-3333-3333-3333-333333333333");
        harness.ThreadRegistry.GetOrCreateAsync(Arg.Any<IEnumerable<Address>>(), Arg.Any<CancellationToken>())
            .Returns(svThreadId.ToString("N"));

        var agentAddr = new Address(Address.AgentScheme, Guid.NewGuid());
        var view = new
        {
            type = "view_submission",
            team = new { id = "T-acme" },
            user = new { id = "U-installer" },
            view = new
            {
                callback_id = SlackCommandDispatcher.SvThreadModalCallbackId,
                state = new
                {
                    values = new Dictionary<string, object>
                    {
                        [SlackCommandDispatcher.SvThreadParticipantsBlockId] = new Dictionary<string, object>
                        {
                            [SlackCommandDispatcher.SvThreadParticipantsActionId] = new
                            {
                                selected_options = new[]
                                {
                                    new { value = agentAddr.ToString() },
                                },
                            },
                        },
                        [SlackCommandDispatcher.SvThreadInitialMessageBlockId] = new Dictionary<string, object>
                        {
                            [SlackCommandDispatcher.SvThreadInitialMessageActionId] = new
                            {
                                value = "let's start",
                            },
                        },
                    },
                },
            },
        };

        var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(view));

        var outcome = await harness.Dispatcher.DispatchInteractionAsync(payload, ct);

        outcome.ShouldBe(SlackCommandDispatchOutcome.Handled);
        await harness.WebApi.Received().OpenConversationAsync(
            "xoxb-test-token", "U-installer", Arg.Any<CancellationToken>());
        await harness.WebApi.Received().PostMessageAsync(
            Arg.Is<string>(s => s == "xoxb-test-token"),
            Arg.Is<string>(s => s == "D-installer"),
            Arg.Is<string>(s => s.StartsWith("sv-", StringComparison.Ordinal)),
            Arg.Is<string?>(s => s == null),
            Arg.Is<string?>(s => s == null),
            Arg.Is<string?>(s => s == null),
            Arg.Any<CancellationToken>());
        await harness.MessageRouter.Received(1).RouteAsync(
            Arg.Is<Message>(m => m.ThreadId == svThreadId.ToString("N")),
            Arg.Any<CancellationToken>());
        harness.AuditLog.Records.ShouldContain(r => r.Disposition == "thread-created");
    }

    private static Dictionary<string, string> BuildForm(string command, string channelName = "directmessage", string channelId = "D1")
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["command"] = command,
            ["team_id"] = "T-acme",
            ["user_id"] = "U-installer",
            ["trigger_id"] = "trigger-1",
            ["channel_id"] = channelId,
            ["channel_name"] = channelName,
            ["text"] = string.Empty,
        };
    }

    private sealed class TestHarness
    {
        public SlackCommandDispatcher Dispatcher { get; }
        public ISlackWebApiClient WebApi { get; }
        public ISlackThreadMapStore ThreadMap { get; }
        public IThreadRegistry ThreadRegistry { get; }
        public IMessageRouter MessageRouter { get; }
        public RecordingAuditLog AuditLog { get; }
        public ITenantConnectorBindingStore BindingStore { get; }

        private TestHarness(
            SlackCommandDispatcher dispatcher,
            ISlackWebApiClient webApi,
            ISlackThreadMapStore threadMap,
            IThreadRegistry threadRegistry,
            IMessageRouter messageRouter,
            RecordingAuditLog auditLog,
            ITenantConnectorBindingStore bindingStore)
        {
            Dispatcher = dispatcher;
            WebApi = webApi;
            ThreadMap = threadMap;
            ThreadRegistry = threadRegistry;
            MessageRouter = messageRouter;
            AuditLog = auditLog;
            BindingStore = bindingStore;
        }

        public static TestHarness Create()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var tenantContext = Substitute.For<ITenantContext>();
            tenantContext.CurrentTenantId.Returns(TestTenantId);
            services.AddSingleton(tenantContext);

            var hatResolver = Substitute.For<ITenantUserHumanResolver>();
            hatResolver.PickFromAsync(
                Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(new Address(Address.HumanScheme, PrimaryHumanId));
            services.AddSingleton(hatResolver);

            var threadRegistry = Substitute.For<IThreadRegistry>();
            services.AddSingleton(threadRegistry);

            var dir = Substitute.For<IDirectoryService>();
            dir.ListAllAsync(Arg.Any<CancellationToken>())
                .Returns(new List<DirectoryEntry>
                {
                    new(
                        new Address(Address.AgentScheme, Guid.NewGuid()),
                        Guid.NewGuid(),
                        "Bob",
                        "An agent",
                        null,
                        DateTimeOffset.UtcNow),
                });
            services.AddSingleton(dir);

            // In-memory EF for the binding store and thread-map store.
            // Capture the db name in a local so every DbContext
            // instantiation shares the same in-memory database.
            var dbName = $"CommandTests_{Guid.NewGuid():N}";
            services.AddDbContext<Cvoya.Spring.Dapr.Data.SpringDbContext>(o =>
                o.UseInMemoryDatabase(dbName));
            services.AddScoped<Cvoya.Spring.Dapr.Data.IUnitConnectorBindingRepository, Cvoya.Spring.Dapr.Data.UnitConnectorBindingRepository>();
            services.AddScoped<Cvoya.Spring.Dapr.Data.ITenantConnectorBindingRepository, Cvoya.Spring.Dapr.Data.TenantConnectorBindingRepository>();
            services.AddSingleton<ITenantConnectorBindingStore, Cvoya.Spring.Dapr.Connectors.TenantConnectorBindingStore>();
            services.AddSingleton<ISlackThreadMapStore, Cvoya.Spring.Dapr.Connectors.Slack.EfSlackThreadMapStore>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ITenantBoundUserExtractor, SlackBoundUserExtractor>());

            // Fake secret resolver returning the bot token verbatim.
            var resolver = Substitute.For<ISecretResolver>();
            resolver.ResolveWithPathAsync(Arg.Any<SecretRef>(), Arg.Any<CancellationToken>())
                .Returns(new SecretResolution(
                    Value: "xoxb-test-token",
                    Path: SecretResolvePath.Direct,
                    EffectiveRef: new SecretRef(SecretScope.Tenant, TestTenantId, "slack/T-acme/bot-token"),
                    Version: 1));
            services.AddSingleton(resolver);

            var webApi = Substitute.For<ISlackWebApiClient>();
            webApi.OpenConversationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new SlackOpenConversationResult(Ok: true, Error: null, ChannelId: "D-installer"));
            webApi.PostMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new SlackPostMessageResult(Ok: true, Error: null, ChannelId: "D-installer", MessageTs: "1700.001"));
            webApi.ViewsOpenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
                .Returns(new SlackResult(Ok: true, Error: null));
            services.AddSingleton(webApi);

            var nameResolver = Substitute.For<IParticipantDisplayNameResolver>();
            nameResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var addr = call.ArgAt<string>(0);
                    return new ValueTask<string>(addr);
                });
            services.AddSingleton(nameResolver);

            var auditLog = new RecordingAuditLog();
            services.AddSingleton<ISlackInboundAuditLog>(auditLog);

            var messageRouter = Substitute.For<IMessageRouter>();
            messageRouter.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
                .Returns(Cvoya.Spring.Core.Result<Message?, RoutingError>.Success(null!));
            services.AddSingleton(messageRouter);

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var slugBuilder = new SlackThreadSlugBuilder(scopeFactory);
            services.AddSingleton<ISlackThreadSlugBuilder>(slugBuilder);
            // Rebuild the provider after registering the builder we
            // need to inject into the dispatcher.
            provider = services.BuildServiceProvider();
            scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var dispatcher = new SlackCommandDispatcher(
                scopeFactory,
                webApi,
                provider.GetRequiredService<ISlackThreadMapStore>(),
                provider.GetRequiredService<ISlackThreadSlugBuilder>(),
                auditLog,
                messageRouter,
                NullLoggerFactory.Instance);

            return new TestHarness(
                dispatcher,
                webApi,
                provider.GetRequiredService<ISlackThreadMapStore>(),
                threadRegistry,
                messageRouter,
                auditLog,
                provider.GetRequiredService<ITenantConnectorBindingStore>());
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
                SingleUserMode: true,
                Mode: SlackBindingMode.Workspace,
                BoundUsers: new[]
                {
                    new TenantSlackBoundUser("U-installer", OperatorTenantUserId),
                });
            var configJson = JsonSerializer.SerializeToElement(config);
            await BindingStore.SetAsync(SlackInstallStore.ConnectorSlug, SlackConnectorType.SlackTypeId, configJson, "T-acme", ct);
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
