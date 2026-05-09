// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Reflection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Regression tests for ADR-0039 C2: UnitActor domain dispatch no longer
/// resolves or invokes the legacy orchestration strategy stack.
/// </summary>
public class UnitActorStrategyResolverTests
{
    [Fact]
    public void Constructor_DoesNotAcceptStrategyResolverOrStrategy()
    {
        var parameterTypes = typeof(UnitActor)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToArray();

        parameterTypes.Select(t => t.Name)
            .ShouldNotContain(name => name.Contains("OrchestrationStrategy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReceiveAsync_DomainMessage_InvokesRuntimePath()
    {
        var actorId = TestSlugIds.HexFor("runtime-unit");
        var runtimeInvocationPath = Substitute.For<IRuntimeInvocationPath>();
        runtimeInvocationPath
            .InvokeAsync(Arg.Any<Address>(), Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var actor = BuildActor(actorId, runtimeInvocationPath);

        var incoming = new Message(
            Id: Guid.NewGuid(),
            From: Address.For("agent", TestSlugIds.HexFor("sender")),
            To: Address.For("unit", actorId),
            Type: MessageType.Domain,
            ThreadId: Guid.NewGuid().ToString(),
            Payload: System.Text.Json.JsonSerializer.SerializeToElement(new { }),
            Timestamp: DateTimeOffset.UtcNow);

        var result = await actor.ReceiveAsync(incoming, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await runtimeInvocationPath.Received(1).InvokeAsync(
            Address.For("unit", actorId),
            incoming,
            Arg.Any<CancellationToken>());
    }

    private static UnitActor BuildActor(string actorId, IRuntimeInvocationPath runtimeInvocationPath)
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId(actorId),
        });

        var actor = new UnitActor(
            host,
            loggerFactory,
            runtimeInvocationPath,
            Substitute.For<IActivityEventBus>(),
            Substitute.For<IDirectoryService>(),
            Substitute.For<IActorProxyFactory>());

        var stateManager = Substitute.For<IActorStateManager>();
        typeof(Actor)
            .GetField("<StateManager>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(actor, stateManager);

        return actor;
    }
}
