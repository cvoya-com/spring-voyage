// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using NSubstitute;

using Xunit;

/// <summary>
/// Smoke test: a unit with no members dispatches through the runtime invocation
/// path (ADR-0039 C2). Verifies the unit-shaped actor behaves identically to
/// an agent-shaped actor for leaf dispatch.
/// </summary>
public class UnitNoMembersResponds
{
    [Fact]
    public async Task ReceiveAsync_UnitWithNoMembers_InvokesRuntimeInvocationPath()
    {
        var (actor, _, runtimeInvocationPath) = ActorTestHost.CreateUnitActor(actorId: "no-members-unit");
        // Default setup: no members (CreateUnitActor's default stateManager returns false for Members)
        var message = MessageFactory.CreateDomainMessage(toId: "no-members-unit", toType: "unit");

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("no-members-unit")),
            message,
            Arg.Any<CancellationToken>());
    }
}