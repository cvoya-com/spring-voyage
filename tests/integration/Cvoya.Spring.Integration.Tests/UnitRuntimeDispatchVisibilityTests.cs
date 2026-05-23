// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Routing;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Regression coverage for #2208: a domain message to a unit whose runtime
/// cannot dispatch must produce a visible activity error instead of a silent
/// 200-with-no-progress path.
/// </summary>
public class UnitRuntimeDispatchVisibilityTests
{
    [Fact]
    public async Task ReceiveAsync_MisconfiguredUnitRuntime_EmitsErrorOccurredActivity()
    {
        var unitId = new Guid("c05fe862-4fab-4b15-b300-c3b283fdbcba");
        var unitAddress = new Address(Address.UnitScheme, unitId);
        var threadId = Guid.NewGuid().ToString("D");
        var runtimePath = CreateRuntimePath(
            unitAddress,
            $"Unit '{unitAddress.Path}' has no execution configuration; set ai.runtime in the unit YAML.");

        var harness = ActorTestHost.CreateUnitActorWithHarness(
            runtimeInvocationPath: runtimePath,
            actorId: unitAddress.Path);
        var published = new List<ActivityEvent>();
        harness.ActivityEventBus
            .PublishAsync(
                Arg.Do<ActivityEvent>(published.Add),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var message = new Message(
            Guid.NewGuid(),
            new Address(Address.HumanScheme, new Guid("bc4975f8-d239-4565-8c4c-c1064818000c")),
            unitAddress,
            MessageType.Domain,
            threadId,
            JsonSerializer.SerializeToElement("hello there"),
            DateTimeOffset.UtcNow);

        var result = await harness.Actor.ReceiveAsync(message, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        var error = published.Single(e => e.EventType == ActivityEventType.ErrorOccurred);
        error.Severity.ShouldBe(ActivitySeverity.Error);
        error.Source.Id.ShouldBe(unitId);
        error.CorrelationId.ShouldBe(threadId);
        error.Summary.ShouldContain("Dispatch failed:");
        error.Summary.ShouldContain("no execution configuration");
    }

    private static RuntimeInvocationPath CreateRuntimePath(Address subject, string dispatchFailure)
    {
        var definitionProvider = Substitute.For<IAgentDefinitionProvider>();
        definitionProvider
            .GetByIdAsync(subject.Path, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(subject.Path, "Misconfigured unit", "Coordinate the work.", null));

        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher
            .DispatchAsync(
                Arg.Any<Message>(),
                Arg.Any<PromptAssemblyContext>(),
                Arg.Any<CancellationToken>())
            .Throws(new SpringException(dispatchFailure));

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory,
            NullMessageWriterScopeFactory.Create());

        var coordinator = new AgentDispatchCoordinator(
            dispatcher,
            Substitute.For<ILogger<AgentDispatchCoordinator>>());

        return new RuntimeInvocationPath(
            definitionProvider,
            Array.Empty<ISkillRegistry>(),
            coordinator);
    }
}
