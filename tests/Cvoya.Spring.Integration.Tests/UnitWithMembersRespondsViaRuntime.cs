// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using global::Dapr.Actors.Runtime;

using NSubstitute;

using Xunit;

/// <summary>
/// Smoke test: a unit with members still dispatches through the runtime
/// invocation path in the C2-D2 window (ADR-0039 C6). After D2 lands,
/// the runtime may delegate via delegate_to_child; that behaviour is
/// covered by OrchestrationDelegationDecisionIntegrationTests.
/// </summary>
public class UnitWithMembersRespondsViaRuntime
{
    [Fact]
    public async Task ReceiveAsync_UnitWithTwoMembers_InvokesRuntimeInvocationPath()
    {
        var (actor, stateManager, runtimeInvocationPath) =
            ActorTestHost.CreateUnitActor(actorId: "members-unit");

        var member1 = Address.For("agent", TestSlugIds.HexFor("child-agent-1"));
        var member2 = Address.For("agent", TestSlugIds.HexFor("child-agent-2"));

        stateManager.TryGetStateAsync<List<Address>>(StateKeys.Members, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<Address>>(true, [member1, member2]));

        var message = MessageFactory.CreateDomainMessage(toId: "members-unit", toType: "unit");

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("members-unit")),
            message,
            Arg.Any<CancellationToken>());
    }
}