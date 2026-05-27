// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.Slack;
using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connector.Slack.DependencyInjection;
using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Connector.Slack.WebApi;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Connectors.Slack;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Messaging;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Threads;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for the #2818 platform-side delivery wire-up.
/// Exercises the full path
/// <c>MessageDeliveryService.DeliverWithRetryAsync</c> →
/// registered <see cref="IConnectorDeliveryObserver"/> →
/// <see cref="ISlackOutboundDispatcher"/> →
/// <see cref="ISlackWebApiClient"/>.
///
/// <para>
/// The harness boots the Slack connector against an in-memory
/// <see cref="SpringDbContext"/> and seeds a tenant Slack binding with
/// one bound human. A delivery from an SV agent to the bound human's
/// <c>human://</c> address must produce a
/// <see cref="ISlackWebApiClient.OpenConversationAsync"/> +
/// <see cref="ISlackWebApiClient.PostMessageAsync"/> call. A delivery
/// to a non-bound recipient must not.
/// </para>
/// </summary>
public class SlackOutboundDeliveryWireUpIntegrationTests
{
    private static readonly Guid TestTenantId = new("aaaaaaaa-bbbb-cccc-dddd-000000000001");
    private static readonly Guid BoundTenantUserId = new("11111111-1111-1111-1111-111111111111");
    private const string SigningSecret = "test-signing-secret";
    private const string BotToken = "xoxb-test-bot-token";

    [Fact]
    public async Task Delivery_ToBoundHuman_PostsToSlack()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = await Harness.CreateAsync();

        // Caller is an agent; target is a human whose TenantUser is the
        // single bound user in the binding. The thread participants the
        // platform passes to the observer naturally include both.
        var caller = new Address(Address.AgentScheme, new Guid("00000001-0000-0000-0000-000000000000"));
        var target = new Address(Address.HumanScheme, BoundTenantUserId);

        // Defensive: confirm the harness's bound-users wiring is intact
        // before exercising the full path. A misregistered extractor would
        // surface here as zero bound users — the router would route to
        // NoSlackSurface, and the test would fail at the WebApi assertion
        // with no signal of why.
        var bindingStore = harness.Services.GetRequiredService<ITenantConnectorBindingStore>();
        var binding = await bindingStore.GetAsync("slack", ct);
        binding.ShouldNotBeNull("slack binding seed failed");
        var boundUsers = await bindingStore.GetBoundUsersAsync("slack", ct);
        boundUsers.Count.ShouldBe(1, "bound-user extractor did not surface the seeded user");
        boundUsers[0].TenantUserId.ShouldBe(BoundTenantUserId);

        var message = new Message(
            Id: Guid.NewGuid(),
            From: caller,
            To: target,
            Type: MessageType.Domain,
            ThreadId: null,
            Payload: JsonDocument.Parse("\"hello from agent\"").RootElement.Clone(),
            Timestamp: DateTimeOffset.UtcNow);

        await harness.DeliveryService.DeliverWithRetryAsync(caller, target, message, ct);

        // The Slack-side path was exercised end-to-end: open DM, post parent
        // (first-on-thread), persist thread_ts, then the actual reply. The
        // dispatcher posts the slug as the parent message and the body as a
        // reply — we assert PostMessageAsync was called at least once
        // (parent + reply) and OpenConversationAsync was called once.
        await harness.WebApi.Received(1).OpenConversationAsync(
            BotToken,
            "U-installer",
            Arg.Any<CancellationToken>());
        await harness.WebApi.Received(2).PostMessageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delivery_ToAgentRecipient_DoesNotPostToSlack()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = await Harness.CreateAsync();

        // Both caller and target are agents — no Slack-bound participants.
        var caller = new Address(Address.AgentScheme, new Guid("00000001-0000-0000-0000-000000000000"));
        var target = new Address(Address.AgentScheme, new Guid("00000002-0000-0000-0000-000000000000"));

        var message = new Message(
            Id: Guid.NewGuid(),
            From: caller,
            To: target,
            Type: MessageType.Domain,
            ThreadId: null,
            Payload: JsonDocument.Parse("\"agent-to-agent\"").RootElement.Clone(),
            Timestamp: DateTimeOffset.UtcNow);

        await harness.DeliveryService.DeliverWithRetryAsync(caller, target, message, ct);

        await harness.WebApi.DidNotReceive().PostMessageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await harness.WebApi.DidNotReceive().OpenConversationAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delivery_ControlMessage_DoesNotPostToSlack()
    {
        // Control messages (HealthCheck / Cancel / StatusQuery) are
        // infrastructure plumbing. The Slack observer must short-circuit
        // them so the operator's DM does not see internal traffic.
        var ct = TestContext.Current.CancellationToken;
        var harness = await Harness.CreateAsync();

        var caller = new Address(Address.AgentScheme, new Guid("00000001-0000-0000-0000-000000000000"));
        var target = new Address(Address.HumanScheme, BoundTenantUserId);

        var message = new Message(
            Id: Guid.NewGuid(),
            From: caller,
            To: target,
            Type: MessageType.HealthCheck,
            ThreadId: null,
            Payload: JsonDocument.Parse("{}").RootElement.Clone(),
            Timestamp: DateTimeOffset.UtcNow);

        await harness.DeliveryService.DeliverWithRetryAsync(caller, target, message, ct);

        await harness.WebApi.DidNotReceive().PostMessageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delivery_NoBindingConfigured_DoesNotThrow()
    {
        // Tenant has no Slack binding — the observer must silently no-op so
        // the platform's delivery contract is unaffected by Slack not being
        // installed.
        var ct = TestContext.Current.CancellationToken;
        var harness = await Harness.CreateAsync(seedBinding: false);

        var caller = new Address(Address.AgentScheme, new Guid("00000001-0000-0000-0000-000000000000"));
        var target = new Address(Address.HumanScheme, BoundTenantUserId);

        var message = new Message(
            Id: Guid.NewGuid(),
            From: caller,
            To: target,
            Type: MessageType.Domain,
            ThreadId: null,
            Payload: JsonDocument.Parse("\"hello\"").RootElement.Clone(),
            Timestamp: DateTimeOffset.UtcNow);

        await Should.NotThrowAsync(async () =>
            await harness.DeliveryService.DeliverWithRetryAsync(caller, target, message, ct));

        await harness.WebApi.DidNotReceive().PostMessageAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        public required IServiceProvider Services { get; init; }
        public required MessageDeliveryService DeliveryService { get; init; }
        public required ISlackWebApiClient WebApi { get; init; }

        public static async Task<Harness> CreateAsync(bool seedBinding = true)
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

            // Configuration — needed by AddCvoyaSpringConnectorSlack for the
            // OAuth options section. Empty config is fine; the harness uses
            // direct DB-seeded binding state, not OAuth flow.
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

            // Tenant context + EF stack (in-memory DB).
            var tenantContext = Substitute.For<ITenantContext>();
            tenantContext.CurrentTenantId.Returns(TestTenantId);
            services.AddSingleton(tenantContext);
            var dbName = $"SlackWireUpIntegration_{Guid.NewGuid():N}";
            services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(dbName));

            // EF-backed binding stores (the production composition for OSS).
            services.AddScoped<IUnitConnectorBindingRepository, UnitConnectorBindingRepository>();
            services.AddScoped<ITenantConnectorBindingRepository, TenantConnectorBindingRepository>();
            services.AddSingleton<ITenantConnectorBindingStore, TenantConnectorBindingStore>();

            // EF-backed Slack thread-map store (production composition).
            services.AddSingleton<ISlackThreadMapStore, EfSlackThreadMapStore>();

            // Slack connector — the system under test. Brings in
            // SlackOutboundDeliveryObserver via IConnectorDeliveryObserver.
            services.AddCvoyaSpringConnectorSlack(
                new ConfigurationBuilder().Build());

            // Override the Slack Web API client with a substitute so we can
            // assert what Slack calls happened without making live HTTP.
            services.RemoveAll<ISlackWebApiClient>();
            var webApi = Substitute.For<ISlackWebApiClient>();
            webApi.OpenConversationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new SlackOpenConversationResult(Ok: true, Error: null, ChannelId: "D-test"));
            webApi.PostMessageAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
                .Returns(args => new SlackPostMessageResult(
                    Ok: true,
                    Error: null,
                    ChannelId: (string)args[1],
                    MessageTs: "1700000000.123456"));
            services.AddSingleton(webApi);

            // Secret resolver — bot token comes back from the resolver.
            var secretResolver = Substitute.For<ISecretResolver>();
            secretResolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Name == "slack/T-acme/bot-token"),
                Arg.Any<CancellationToken>())
                .Returns(new SecretResolution(
                    Value: BotToken,
                    Path: SecretResolvePath.Direct,
                    EffectiveRef: new SecretRef(SecretScope.Tenant, TestTenantId, "slack/T-acme/bot-token"),
                    Version: 1));
            secretResolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Name == "slack/T-acme/signing-secret"),
                Arg.Any<CancellationToken>())
                .Returns(new SecretResolution(
                    Value: SigningSecret,
                    Path: SecretResolvePath.Direct,
                    EffectiveRef: new SecretRef(SecretScope.Tenant, TestTenantId, "slack/T-acme/signing-secret"),
                    Version: 1));
            services.AddSingleton(secretResolver);

            // Routing / persistence collaborators the delivery service touches.
            services.AddScoped<IThreadRegistry, NoopThreadRegistry>();
            services.AddScoped<IMessageWriter, NoopMessageWriter>();
            services.AddSingleton(Substitute.For<IMessageTenantResolver>());

            // Hop counter actor — return 1 for every increment so we never
            // exceed MaxHopCount. The delivery hot path opens an actor proxy
            // for the per-thread hop counter but only when EnsureHopBudgetAsync
            // is called explicitly; DeliverWithRetryAsync itself doesn't
            // touch it. We still register the factory so the service can
            // construct without missing dependencies.
            var actorProxyFactory = Substitute.For<IActorProxyFactory>();
            services.AddSingleton(actorProxyFactory);

            // Agent proxy resolver — the recipient mailbox stub.
            var receiverAgent = Substitute.For<IAgent>();
            receiverAgent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
                .Returns((Message?)null);
            var agentProxyResolver = Substitute.For<IAgentProxyResolver>();
            agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(receiverAgent);
            services.AddSingleton(agentProxyResolver);

            // Delivery options — keep retries tight so a failure in the test
            // surfaces fast.
            services.AddSingleton(Options.Create(new MessageDeliveryOptions
            {
                MaxAttempts = 1,
                Budget = TimeSpan.FromSeconds(2),
                InitialBackoff = TimeSpan.FromMilliseconds(1),
                MaxHopCount = 16,
            }));

            // MessageDeliveryService itself.
            services.AddSingleton<MessageDeliveryService>();

            // The Slack outbound dispatcher needs ITenantUserHumanResolver
            // (to map human:// → bound TenantUser) and IHumanTenantUserLookup
            // (the reverse FK). Stub both with the OSS single-user
            // identity: the bound TenantUserId is exactly the address.Id we
            // ship in human://<id>.
            //
            // PickFromAsync returns the Hat for the bound TenantUser; the
            // slug builder uses this to "drop" the operator from the slug
            // (per ADR-0061 §4). The OSS shape is one Human per
            // TenantUser, so the Hat is just human://<boundTenantUserId>.
            var humanResolver = Substitute.For<ITenantUserHumanResolver>();
            humanResolver.PickFromAsync(
                Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(call => new Address(Address.HumanScheme, (Guid)call[0]));
            services.AddScoped(_ => humanResolver);

            var humanLookup = Substitute.For<IHumanTenantUserLookup>();
            humanLookup.GetTenantUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<Guid?>((Guid)call[0]));
            services.AddScoped(_ => humanLookup);

            // Display-name resolver used by the slug builder. The slug
            // builder requires non-empty names per
            // IParticipantDisplayNameResolver's contract; the substitute's
            // default ValueTask<string> returns "", which would corrupt
            // the slug. Return a deterministic non-empty value.
            var nameResolver = Substitute.For<IParticipantDisplayNameResolver>();
            nameResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => ValueTask.FromResult("participant"));
            services.AddSingleton(nameResolver);

            var provider = services.BuildServiceProvider();

            if (seedBinding)
            {
                using var scope = provider.CreateScope();
                var bindingStore = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingStore>();
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
                        new TenantSlackBoundUser("U-installer", BoundTenantUserId),
                    });
                var configJson = JsonSerializer.SerializeToElement(config);
                await bindingStore.SetAsync(
                    SlackInstallStore.ConnectorSlug,
                    SlackConnectorType.SlackTypeId,
                    configJson,
                    "T-acme",
                    CancellationToken.None);
            }

            return new Harness
            {
                Services = provider,
                DeliveryService = provider.GetRequiredService<MessageDeliveryService>(),
                WebApi = webApi,
            };
        }
    }

    /// <summary>
    /// Returns a deterministic thread id derived from the participant set so
    /// the delivery loop can resolve a thread without standing up the full
    /// EF thread registry. The dispatcher only needs a parseable Guid string.
    /// </summary>
    private sealed class NoopThreadRegistry : IThreadRegistry
    {
        private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);

        public Task<string> GetOrCreateAsync(IEnumerable<Address> participants, CancellationToken cancellationToken = default)
        {
            var key = string.Join('|', participants
                .Select(a => $"{a.Scheme}:{a.Id:N}")
                .OrderBy(s => s, StringComparer.Ordinal));
            if (!_byKey.TryGetValue(key, out var id))
            {
                id = Guid.NewGuid().ToString("N");
                _byKey[key] = id;
            }
            return Task.FromResult(id);
        }

        public Task<ThreadRegistryEntry?> ResolveAsync(string threadId, CancellationToken cancellationToken = default)
            => Task.FromResult<ThreadRegistryEntry?>(null);
    }

    /// <summary>
    /// Drops every write — the test doesn't exercise the EF messages table
    /// (the platform-side delivery wire-up assertions look only at the Slack
    /// Web API client). Pinning that path is the EfMessageWriter test bed.
    /// </summary>
    private sealed class NoopMessageWriter : IMessageWriter
    {
        public Task WriteAsync(Message message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
