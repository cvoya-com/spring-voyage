// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the UnitActor ↔ <see cref="IOrchestrationStrategyResolver"/>
/// seam introduced by #491. Exercises a real actor construction with a
/// substituted resolver so the actor's per-message lookup path is covered
/// alongside the resolver-only unit tests under Dapr.Tests.
/// </summary>
public class ManifestStrategyResolverIntegrationTests
{
    [Fact]
    public async Task ReceiveAsync_DomainMessage_ConsultsResolverAndUsesReturnedStrategy()
    {
        var resolverStrategy = Substitute.For<IOrchestrationStrategy>();
        var resolver = Substitute.For<IOrchestrationStrategyResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrchestrationStrategyLease(resolverStrategy, "label-routed"));

        var (actor, _, _, fallbackStrategy) = ActorTestHost.CreateUnitActorWithResolver(
            actorId: "triage-team",
            resolver: resolver);

        var message = MessageFactory.CreateDomainMessage(toId: "triage-team", toType: "unit");
        resolverStrategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Message?>(null));

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await resolver.Received(1).ResolveAsync("triage-team", Arg.Any<CancellationToken>());
        await resolverStrategy.Received(1).OrchestrateAsync(
            message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>());
        await fallbackStrategy.DidNotReceive().OrchestrateAsync(
            Arg.Any<Message>(), Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_NoResolver_KeepsUsingInjectedStrategy()
    {
        var (actor, _, strategy) = ActorTestHost.CreateUnitActor(actorId: "legacy-team");
        var message = MessageFactory.CreateDomainMessage(toId: "legacy-team", toType: "unit");
        strategy.OrchestrateAsync(message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Message?>(null));

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        // Without a resolver wired (pre-#491 / legacy test path) the unit
        // actor continues to dispatch through its constructor-injected
        // strategy — the backward-compat gate.
        await strategy.Received(1).OrchestrateAsync(
            message, Arg.Any<IUnitContext>(), Arg.Any<CancellationToken>());
    }
}