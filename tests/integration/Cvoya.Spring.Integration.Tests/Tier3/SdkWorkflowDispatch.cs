// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests.Tier3;

using System.Text.Json;

using Cvoya.Spring.AgentSdk;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dispatcher;
using Cvoya.Spring.Dispatcher.Auth;
using Cvoya.Spring.Sample.WorkflowAgent;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

public class SdkWorkflowDispatch
{
    private static readonly Address UnitAddress =
        new(Address.UnitScheme, Guid.Parse("bbbbbbbb-0000-0000-0000-000000001835"));

    private static readonly Address Child0Address =
        new(Address.AgentScheme, Guid.Parse("aaaaaaaa-0000-0000-0000-000000001835"));

    private static readonly Address Child1Address =
        new(Address.AgentScheme, Guid.Parse("aaaaaaaa-0000-0000-0000-000000001836"));

    [Fact]
    public async Task RunAsync_CodeMessage_DelegatesThroughSdkAndEmitsRoutedDecisionEvent()
    {
        var threadId = Guid.Parse("eeeeeeee-0000-0000-0000-000000001835");
        var inputMessageId = Guid.Parse("dddddddd-0000-0000-0000-000000001835");

        // ADR-0049 — delegate_to is a one-way delivery; the child mailbox
        // enqueue (ReceiveAsync) returns no work product.
        var child0 = Substitute.For<IAgent>();
        child0.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        var child1 = Substitute.For<IAgent>();
        child1.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        await using var server = await SdkWorkflowTestServer.StartAsync(
            UnitAddress,
            [Child0Address, Child1Address],
            new Dictionary<Address, IAgent>
            {
                [Child0Address] = child0,
                [Child1Address] = child1,
            },
            TestContext.Current.CancellationToken);

        using var _ = new EnvironmentScope(
            ("SPRING_CHILD_0", Child0Address.ToString()),
            ("SPRING_CHILD_1", Child1Address.ToString()));

        var client = new OrchestrationClient(
            server.BaseUrl,
            server.IssueToken(UnitAddress, threadId, inputMessageId));

        var result = await WorkflowStateMachine.RunAsync(
            client,
            threadId.ToString("D"),
            "please write code",
            TestContext.Current.CancellationToken);

        // ADR-0049 — RunAsync now reports the delivery acknowledgement, not
        // the child's work product.
        result.ShouldContain("Delegated to");
        result.ShouldContain(Child0Address.ToString());

        server.PublishedEvents.Count.ShouldBe(1);
        var activityEvent = server.PublishedEvents.Single();
        activityEvent.Source.ShouldBe(UnitAddress);
        activityEvent.EventType.ShouldBe(ActivityEventType.DecisionMade);
        activityEvent.Details.ShouldNotBeNull();

        var decision = JsonSerializer.Deserialize<OrchestrationDecision>(
            activityEvent.Details!.Value.GetRawText());

        decision.ShouldNotBeNull();
        decision!.TenantId.ShouldBe(OssTenantIds.Default);
        decision.TenantId.ShouldNotBe(Guid.Empty);
        decision.UnitAddress.ShouldBe(UnitAddress);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(inputMessageId);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Delegate);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Routed);
        decision.Targets.ShouldBe([Child0Address]);
        decision.ResultMessageIds.ShouldBeEmpty();
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly (string Name, string? Value)[] _previousValues;

        public EnvironmentScope(params (string Name, string Value)[] values)
        {
            _previousValues = values
                .Select(value => (value.Name, Environment.GetEnvironmentVariable(value.Name)))
                .ToArray();

            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previousValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private sealed class SdkWorkflowTestServer : IAsyncDisposable
    {
        private static readonly byte[] SigningKey =
        [
            0x30, 0x9d, 0xe3, 0xf8, 0x4d, 0x02, 0x5d, 0xaf,
            0x76, 0x11, 0xc8, 0x96, 0x4e, 0x61, 0x73, 0x0b,
            0x44, 0x8e, 0x26, 0x74, 0x95, 0xe2, 0xab, 0x19,
            0xda, 0xc4, 0x31, 0x82, 0x07, 0xbd, 0x58, 0x6f,
        ];

        private readonly WebApplication _app;
        private readonly ITenantSigningKeyProvider _keyProvider;

        private SdkWorkflowTestServer(
            WebApplication app,
            string baseUrl,
            ITenantSigningKeyProvider keyProvider,
            CapturingActivityEventBus activityEventBus)
        {
            _app = app;
            BaseUrl = baseUrl;
            _keyProvider = keyProvider;
            PublishedEvents = activityEventBus.Events;
        }

        public string BaseUrl { get; }

        public List<ActivityEvent> PublishedEvents { get; }

        public static async Task<SdkWorkflowTestServer> StartAsync(
            Address unit,
            Address[] members,
            IReadOnlyDictionary<Address, IAgent> agents,
            CancellationToken cancellationToken)
        {
            var keyProvider = Substitute.For<ITenantSigningKeyProvider>();
            keyProvider.GetSigningKey(Arg.Any<Guid>()).Returns(SigningKey);

            var activityEventBus = new CapturingActivityEventBus();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            builder.Services.AddSingleton(keyProvider);
            builder.Services.AddSingleton(Options.Create(new CallbackTokenOptions()));
            builder.Services.AddSingleton<CallbackTokenValidator>();
            builder.Services.AddSingleton<OrchestrationCallbackDiagnostics>();
            builder.Services.AddSingleton(CreateActorProxyFactory(unit, members));
            builder.Services.AddSingleton(CreateAgentProxyResolver(agents));
            builder.Services.AddSingleton<IActivityEventBus>(activityEventBus);
            // ADR-0039 §3 gate 6 — single-tenant resolver is the OSS default.
            builder.Services.AddSingleton<IOrchestrationTenantResolver, SingleTenantOrchestrationTenantResolver>();
            builder.Services.AddSingleton<OrchestrationToolHandlers>();
            // The MCP root handler registered by MapOrchestrationCallbackEndpoints
            // resolves IOrchestrationToolProvider for its tools/list response.
            builder.Services.AddSingleton<IOrchestrationToolProvider, DirectoryOrchestrationToolProvider>();

            var app = builder.Build();
            app.MapOrchestrationCallbackEndpoints();
            await app.StartAsync(cancellationToken);

            var addressFeature = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>();
            var baseUrl = addressFeature?.Addresses.Single()
                ?? throw new InvalidOperationException("Kestrel did not expose a bound address.");

            return new SdkWorkflowTestServer(app, baseUrl, keyProvider, activityEventBus);
        }

        public string IssueToken(Address caller, Guid threadId, Guid messageId)
        {
            var issuer = new CallbackTokenIssuer(
                _keyProvider,
                Options.Create(new CallbackTokenOptions()));

            return issuer.Issue(new CallbackToken(
                OssTenantIds.Default,
                caller,
                threadId,
                messageId,
                ExpiresAt: default));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static IActorProxyFactory CreateActorProxyFactory(Address unit, Address[] members)
        {
            var proxyFactory = Substitute.For<IActorProxyFactory>();
            proxyFactory.CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), nameof(UnitActor))
                .Returns(call =>
                {
                    var actorId = call.ArgAt<ActorId>(0).GetId();
                    var actor = Substitute.For<IUnitActor>();
                    var actorMembers = string.Equals(actorId, GuidFormatter.Format(unit.Id), StringComparison.Ordinal)
                        ? members
                        : [];
                    actor.GetMembersAsync(Arg.Any<CancellationToken>()).Returns(actorMembers);
                    return actor;
                });

            return proxyFactory;
        }

        private static IAgentProxyResolver CreateAgentProxyResolver(IReadOnlyDictionary<Address, IAgent> agents)
        {
            var resolver = Substitute.For<IAgentProxyResolver>();
            var index = agents.ToDictionary(
                pair => $"{pair.Key.Scheme}:{GuidFormatter.Format(pair.Key.Id)}",
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

            resolver.Resolve(Arg.Any<string>(), Arg.Any<string>())
                .Returns(call =>
                {
                    var scheme = call.ArgAt<string>(0);
                    var actorId = call.ArgAt<string>(1);
                    return index.TryGetValue($"{scheme}:{actorId}", out var agent)
                        ? agent
                        : null;
                });

            return resolver;
        }
    }

    private sealed class CapturingActivityEventBus : IActivityEventBus
    {
        public List<ActivityEvent> Events { get; } = [];

        public IObservable<ActivityEvent> ActivityStream { get; } = new EmptyObservable<ActivityEvent>();

        public Task PublishAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(activityEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyObservable<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) => EmptyDisposable.Instance;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
