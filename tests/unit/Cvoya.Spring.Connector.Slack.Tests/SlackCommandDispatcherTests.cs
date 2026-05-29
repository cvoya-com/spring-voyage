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
using NSubstitute.ExceptionExtensions;

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
    public async Task DispatchAsync_SvThreads_FromDm_RendersCanonicalPermalinks()
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

        // chat.getPermalink should fire once per unique (channel, ts).
        await harness.WebApi.Received(1).GetPermalinkAsync(
            "xoxb-test-token", "D-installer", "1700.1", Arg.Any<CancellationToken>());
        await harness.WebApi.Received(1).GetPermalinkAsync(
            "xoxb-test-token", "D-installer", "1700.2", Arg.Any<CancellationToken>());

        // The view should embed the canonical HTTPS permalinks rather
        // than the workspace-local slack:// URI (ADR-0061 §5 / #2844).
        var view = harness.CapturedThreadsView.ShouldNotBeNull();
        var rowTexts = ReadSectionRowTexts(view);
        rowTexts.ShouldContain(t => t.Contains("https://acme.slack.com/archives/D-installer/p17001", StringComparison.Ordinal));
        rowTexts.ShouldContain(t => t.Contains("https://acme.slack.com/archives/D-installer/p17002", StringComparison.Ordinal));
        rowTexts.ShouldNotContain(t => t.Contains("slack://", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchAsync_SvThreads_PermalinkOkFalse_FallsBackToSlackUri()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        await harness.SeedBindingAsync(ct);

        // chat.getPermalink returns ok=false — common when the bot
        // lost access to the message or the workspace rate-limited us.
        harness.WebApi.GetPermalinkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SlackPermalinkResult(Ok: false, Error: "channel_not_found", Permalink: string.Empty));

        await harness.ThreadMap.RecordAsync(
            Guid.NewGuid(), OperatorTenantUserId, "T-acme", "D-installer", "1700.1", ct);

        var form = BuildForm(SlackCommandDispatcher.SvThreadsCommand, channelName: "directmessage");
        var outcome = await harness.Dispatcher.DispatchAsync(form, ct);

        outcome.ShouldBe(SlackCommandDispatchOutcome.Handled);
        var rowTexts = ReadSectionRowTexts(harness.CapturedThreadsView.ShouldNotBeNull());
        rowTexts.ShouldContain(t => t.Contains("slack://channel?team=T-acme&id=D-installer&message=1700.1", StringComparison.Ordinal));
        rowTexts.ShouldNotContain(t => t.Contains("https://", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchAsync_SvThreads_PermalinkThrows_FallsBackToSlackUri()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        await harness.SeedBindingAsync(ct);

        // chat.getPermalink throws — transport error, budget exceeded,
        // or any other unexpected fault. Per ADR-0061 §5 the row
        // falls back to the workspace-local slack:// URI.
        harness.WebApi.GetPermalinkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new OperationCanceledException());

        await harness.ThreadMap.RecordAsync(
            Guid.NewGuid(), OperatorTenantUserId, "T-acme", "D-installer", "1700.1", ct);

        var form = BuildForm(SlackCommandDispatcher.SvThreadsCommand, channelName: "directmessage");
        var outcome = await harness.Dispatcher.DispatchAsync(form, ct);

        outcome.ShouldBe(SlackCommandDispatchOutcome.Handled);
        var rowTexts = ReadSectionRowTexts(harness.CapturedThreadsView.ShouldNotBeNull());
        rowTexts.ShouldContain(t => t.Contains("slack://channel?team=T-acme&id=D-installer&message=1700.1", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> ReadSectionRowTexts(JsonElement view)
    {
        var result = new List<string>();
        foreach (var block in view.GetProperty("blocks").EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type)
                && type.ValueEquals("section")
                && block.TryGetProperty("text", out var text)
                && text.TryGetProperty("text", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                result.Add(value.GetString() ?? string.Empty);
            }
        }
        return result;
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
        var payload = BuildViewSubmissionPayload(new[] { agentAddr }, initialMessage: "let's start");

        var response = await harness.Dispatcher.DispatchInteractionAsync(payload, ct);

        response.Outcome.ShouldBe(SlackCommandDispatchOutcome.Handled);
        response.ResponseBody.ShouldBeNull();

        // The actual thread-creation work runs on a background task
        // (#2879). Drain it before asserting on web-API receives.
        await harness.Dispatcher.LastBackgroundTask;

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

    /// <summary>
    /// #2879: Slack's view_submission ack budget is 3 seconds; a slow
    /// chat.postMessage on the background path must not delay the
    /// dispatch return value. The dispatch call must complete well
    /// inside the budget, and the background work must still run.
    /// </summary>
    [Fact]
    public async Task DispatchInteractionAsync_ViewSubmission_SlowPostMessage_AcksWithinBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        await harness.SeedBindingAsync(ct);

        var svThreadId = new Guid("44444444-4444-4444-4444-444444444444");
        harness.ThreadRegistry.GetOrCreateAsync(Arg.Any<IEnumerable<Address>>(), Arg.Any<CancellationToken>())
            .Returns(svThreadId.ToString("N"));

        // Gate chat.postMessage on a TaskCompletionSource so the test
        // controls when the background work makes progress. The gate
        // simulates Slack-side latency well beyond the 3-second budget.
        var postMessageGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.WebApi.PostMessageAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await postMessageGate.Task.ConfigureAwait(false);
                return new SlackPostMessageResult(Ok: true, Error: null, ChannelId: "D-installer", MessageTs: "1700.001");
            });

        var agentAddr = new Address(Address.AgentScheme, Guid.NewGuid());
        var payload = BuildViewSubmissionPayload(new[] { agentAddr }, initialMessage: null);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await harness.Dispatcher.DispatchInteractionAsync(payload, ct);
        stopwatch.Stop();

        // Hard bound: dispatch must return well within Slack's 3-second
        // ack budget regardless of downstream Slack-API latency.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        response.Outcome.ShouldBe(SlackCommandDispatchOutcome.Handled);
        response.ResponseBody.ShouldBeNull();

        // Background work is still in flight at this point — release
        // the gate and confirm it completes with a recorded audit.
        var backgroundTask = harness.Dispatcher.LastBackgroundTask;
        backgroundTask.IsCompleted.ShouldBeFalse();
        postMessageGate.SetResult(true);
        await backgroundTask;

        harness.AuditLog.Records.ShouldContain(r => r.Disposition == "thread-created");
        await harness.WebApi.Received(1).PostMessageAsync(
            "xoxb-test-token", "D-installer", Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// #2879: an empty participants list must return Slack's
    /// response_action=errors shape so the modal stays open with the
    /// field error highlighted instead of closing silently.
    /// </summary>
    [Fact]
    public async Task DispatchInteractionAsync_ViewSubmission_EmptyParticipants_ReturnsResponseActionErrors()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        await harness.SeedBindingAsync(ct);

        var payload = BuildViewSubmissionPayload(Array.Empty<Address>(), initialMessage: null);

        var response = await harness.Dispatcher.DispatchInteractionAsync(payload, ct);

        response.Outcome.ShouldBe(SlackCommandDispatchOutcome.Handled);
        response.ResponseBody.ShouldNotBeNull();

        // Serialise the response body and assert the Slack-expected shape.
        var bodyJson = JsonSerializer.SerializeToElement(response.ResponseBody);
        bodyJson.GetProperty("response_action").GetString().ShouldBe("errors");
        var errors = bodyJson.GetProperty("errors");
        errors.TryGetProperty(SlackCommandDispatcher.SvThreadParticipantsBlockId, out var participantsError).ShouldBeTrue();
        participantsError.GetString().ShouldNotBeNullOrEmpty();

        // Validation failure should NOT touch Slack web APIs.
        await harness.WebApi.DidNotReceive().OpenConversationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.WebApi.DidNotReceive().PostMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        harness.AuditLog.Records.ShouldContain(r =>
            r.Disposition == "validation-failed" && r.Detail == "no-participants");
    }

    private static JsonElement BuildViewSubmissionPayload(
        IReadOnlyList<Address> selectedAddresses,
        string? initialMessage)
    {
        var selectedOptions = selectedAddresses
            .Select(a => (object)new { value = a.ToString() })
            .ToArray();

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
                                selected_options = selectedOptions,
                            },
                        },
                        [SlackCommandDispatcher.SvThreadInitialMessageBlockId] = new Dictionary<string, object>
                        {
                            [SlackCommandDispatcher.SvThreadInitialMessageActionId] = new
                            {
                                value = initialMessage,
                            },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(view));
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
        public JsonElement? CapturedThreadsView { get; set; }

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
            // Default: chat.getPermalink succeeds with a synthetic
            // canonical URL derived from (channel, ts). Individual
            // tests override this to exercise the fallback paths.
            webApi.GetPermalinkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => new SlackPermalinkResult(
                    Ok: true,
                    Error: null,
                    Permalink: $"https://acme.slack.com/archives/{call.ArgAt<string>(1)}/p{call.ArgAt<string>(2).Replace(".", string.Empty, StringComparison.Ordinal)}"));
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

            var harness = new TestHarness(
                dispatcher,
                webApi,
                provider.GetRequiredService<ISlackThreadMapStore>(),
                threadRegistry,
                messageRouter,
                auditLog,
                provider.GetRequiredService<ITenantConnectorBindingStore>());

            // Capture views opened during the test so we can assert on
            // the rendered Block Kit payload (e.g. /sv-threads links).
            webApi.ViewsOpenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    harness.CapturedThreadsView = call.ArgAt<JsonElement>(2);
                    return new SlackResult(Ok: true, Error: null);
                });

            return harness;
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
