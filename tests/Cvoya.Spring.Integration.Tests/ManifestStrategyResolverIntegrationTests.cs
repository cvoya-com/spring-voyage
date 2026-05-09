// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Regression tests for the ADR-0039 C2 removal of UnitActor's
/// manifest-strategy-resolution path.
/// </summary>
public class ManifestStrategyResolverIntegrationTests
{
    [Fact]
    public async Task ReceiveAsync_DomainMessage_UsesRuntimePath()
    {
        var (actor, _, runtimeInvocationPath) = ActorTestHost.CreateUnitActor(actorId: "triage-team");
        var message = MessageFactory.CreateDomainMessage(toId: "triage-team", toType: "unit");

        await actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", TestSlugIds.HexFor("triage-team")),
            message,
            Arg.Any<CancellationToken>());
    }
}
