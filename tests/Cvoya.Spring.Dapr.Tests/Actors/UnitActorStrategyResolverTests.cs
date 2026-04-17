// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Reflection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitActor"/>'s interaction with the
/// <see cref="IOrchestrationStrategyResolver"/> added in #491. Verifies the
/// actor consults the resolver per message when one is wired (production
/// path) and keeps dispatching through the unkeyed strategy when no
/// resolver is supplied (the pre-#491 default, preserved for test
/// harnesses that construct the actor directly).
/// </summary>
public class UnitActorStrategyResolverTests
{
    [Fact]
    public async Task HandleDomainMessage_WithResolver_ResolvesPerMessage()
    {
        var resolverStrategy = Substitute.For<IOrchestrationStrategy>();
        var defaultStrategy = Substitute.For<IOrchestrationStrategy>();
        var resolver = Substitute.For<IOrchestrationStrategyResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrchestrationStrategyLease(resolverStrategy, "label-routed"));

        var actor = BuildActor(defaultStrategy, resolver);

        var incoming = new Message(
            Id: Guid.NewGuid(),
            From: new Address("agent", "sender"),
            To: new Address("unit", "resolver-unit"),
            Type: MessageType.Domain,
            ConversationId: Guid.NewGuid().ToString(),
            Payload: System.Text.Json.JsonSerializer.SerializeToElement(new { }),
            Timestamp: DateTimeOffset.UtcNow);

        await actor.ReceiveAsync(incoming, TestContext.Current.CancellationToken);

        await resolver.Received(1).ResolveAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await resolverStrategy.Received(1).OrchestrateAsync(
            incoming, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>());
        await defaultStrategy.DidNotReceive().OrchestrateAsync(
            Arg.Any<Message>(), Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDomainMessage_NoResolver_FallsBackToInjectedStrategy()
    {
        var defaultStrategy = Substitute.For<IOrchestrationStrategy>();

        var actor = BuildActor(defaultStrategy, resolver: null);

        var incoming = new Message(
            Id: Guid.NewGuid(),
            From: new Address("agent", "sender"),
            To: new Address("unit", "bare-unit"),
            Type: MessageType.Domain,
            ConversationId: Guid.NewGuid().ToString(),
            Payload: System.Text.Json.JsonSerializer.SerializeToElement(new { }),
            Timestamp: DateTimeOffset.UtcNow);

        await actor.ReceiveAsync(incoming, TestContext.Current.CancellationToken);

        await defaultStrategy.Received(1).OrchestrateAsync(
            incoming, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDomainMessage_WithResolver_DisposesLease()
    {
        var resolverStrategy = Substitute.For<IOrchestrationStrategy>();
        var scope = Substitute.For<IAsyncDisposable>();
        scope.DisposeAsync().Returns(ValueTask.CompletedTask);

        var resolver = Substitute.For<IOrchestrationStrategyResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrchestrationStrategyLease(resolverStrategy, "ai", scope));

        var actor = BuildActor(Substitute.For<IOrchestrationStrategy>(), resolver);

        var incoming = new Message(
            Id: Guid.NewGuid(),
            From: new Address("agent", "sender"),
            To: new Address("unit", "scope-unit"),
            Type: MessageType.Domain,
            ConversationId: Guid.NewGuid().ToString(),
            Payload: System.Text.Json.JsonSerializer.SerializeToElement(new { }),
            Timestamp: DateTimeOffset.UtcNow);

        await actor.ReceiveAsync(incoming, TestContext.Current.CancellationToken);

        await scope.Received(1).DisposeAsync();
    }

    private static UnitActor BuildActor(
        IOrchestrationStrategy defaultStrategy,
        IOrchestrationStrategyResolver? resolver)
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId("resolver-test-unit"),
        });

        var actor = new UnitActor(
            host,
            loggerFactory,
            defaultStrategy,
            Substitute.For<IActivityEventBus>(),
            Substitute.For<IDirectoryService>(),
            Substitute.For<IActorProxyFactory>(),
            expertiseSeedProvider: null,
            strategyResolver: resolver);

        var stateManager = Substitute.For<IActorStateManager>();
        stateManager
            .TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(false, default!));

        typeof(Actor)
            .GetField("<StateManager>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(actor, stateManager);

        return actor;
    }
}