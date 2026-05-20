// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the routing-decision activity <see cref="AgentDispatchCoordinator"/>
/// emits after a connector-origin turn terminates (issue #2560). The
/// coordinator records the routing <em>outcome</em> of a connector event —
/// including the "no agent dispatched" case the activity stream was
/// previously silent on — from the deterministic platform-host side.
/// </summary>
public class AgentDispatchCoordinatorTests
{
    private static readonly Address ConnectorFrom =
        new(Address.ConnectorScheme, new Guid("00000000-0000-0000-0000-006769746875"));

    private static readonly Address UnitTo =
        Address.For(Address.UnitScheme, TestSlugIds.HexFor("triage-unit"));

    private static MessageRouter MakeRouter()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        return Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory,
            NullMessageWriterScopeFactory.Create());
    }

    private static AgentDispatchCoordinator MakeCoordinator(IExecutionDispatcher dispatcher)
        => new(
            dispatcher,
            MakeRouter(),
            Substitute.For<ILogger<AgentDispatchCoordinator>>());

    /// <summary>
    /// A GitHub-shaped connector domain message — the synthetic
    /// <c>connector://</c> provenance address (ADR-0048) plus the
    /// translated issue payload <c>GitHubWebhookHandler</c> builds.
    /// </summary>
    private static Message MakeConnectorMessage(string action = "unlabeled", int issueNumber = 2535)
        => new(
            Guid.NewGuid(),
            ConnectorFrom,
            UnitTo,
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new
            {
                source = "github",
                intent = "label_change",
                action,
                issue = new { number = issueNumber, title = "Flaky test" },
            }),
            DateTimeOffset.UtcNow);

    private static Message MakeAgentMessage()
        => new(
            Guid.NewGuid(),
            Address.For(Address.AgentScheme, TestSlugIds.HexFor("sender")),
            UnitTo,
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { text = "hello" }),
            DateTimeOffset.UtcNow);

    private static Message MakeDispatchResponse(int exitCode = 0)
        => new(
            Guid.NewGuid(),
            UnitTo,
            ConnectorFrom,
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Output = "done", ExitCode = exitCode }),
            DateTimeOffset.UtcNow);

    private static async Task<List<ActivityEvent>> RunAsync(
        AgentDispatchCoordinator coordinator,
        Message inbound)
    {
        var emitted = new List<ActivityEvent>();
        await coordinator.RunDispatchAsync(
            agentId: inbound.To.Path,
            message: inbound,
            context: null!,
            emitActivity: (e, _) => { emitted.Add(e); return Task.CompletedTask; },
            onDispatchExit: _ => Task.CompletedTask);
        return emitted;
    }

    [Fact]
    public async Task RunDispatchAsync_ConnectorEvent_NoDelegation_EmitsDecisionMadeNoOp()
    {
        // #2560 acceptance criterion 2: a connector event the unit processed
        // without dispatching an agent must still leave a DecisionMade row —
        // the stream was previously silent on this "no action" outcome.
        var inbound = MakeConnectorMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(MakeDispatchResponse());

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        var decision = emitted
            .Where(e => e.EventType == ActivityEventType.DecisionMade)
            .ShouldHaveSingleItem();
        decision.Severity.ShouldBe(ActivitySeverity.Info);
        decision.CorrelationId.ShouldBe(inbound.ThreadId);

        var details = decision.Details.ShouldNotBeNull();
        details.GetProperty("decision").GetString().ShouldBe("event_processed");
        details.GetProperty("connectorEventType").GetString().ShouldBe("github.unlabeled");
        details.GetProperty("entityKind").GetString().ShouldBe("issue");
        details.GetProperty("entityReference").GetString().ShouldBe("2535");
        details.GetProperty("threadId").GetString().ShouldBe(inbound.ThreadId);
    }

    [Fact]
    public async Task RunDispatchAsync_ConnectorEvent_FailedTurn_EmitsDecisionMadeFailed()
    {
        var inbound = MakeConnectorMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(MakeDispatchResponse(exitCode: 1));

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        var decision = emitted
            .Where(e => e.EventType == ActivityEventType.DecisionMade)
            .ShouldHaveSingleItem();
        decision.Severity.ShouldBe(ActivitySeverity.Warning);
        decision.CorrelationId.ShouldBe(inbound.ThreadId);
        decision.Details.ShouldNotBeNull()
            .GetProperty("decision").GetString().ShouldBe("processing_failed");
    }

    [Fact]
    public async Task RunDispatchAsync_ConnectorEvent_NoResponse_EmitsDecisionMade()
    {
        // A connector turn the runtime ran but returned nothing recordable
        // is still a routing outcome — the stream must not go silent.
        var inbound = MakeConnectorMessage(action: "labeled");
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        var decision = emitted
            .Where(e => e.EventType == ActivityEventType.DecisionMade)
            .ShouldHaveSingleItem();
        decision.CorrelationId.ShouldBe(inbound.ThreadId);
        decision.Details.ShouldNotBeNull()
            .GetProperty("connectorEventType").GetString().ShouldBe("github.labeled");
    }

    [Fact]
    public async Task RunDispatchAsync_NonConnectorEvent_DoesNotEmitDecisionMade()
    {
        // Agent-to-agent / human-originated turns are not connector routing
        // decisions — emitting DecisionMade there would only add noise.
        var inbound = MakeAgentMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(MakeDispatchResponse());

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        emitted.ShouldNotContain(e => e.EventType == ActivityEventType.DecisionMade);
    }

    [Fact]
    public async Task RunDispatchAsync_ConnectorEvent_DecisionMadeShareCorrelationIdWithRecordedResponse()
    {
        // #2560 acceptance criterion 3: every downstream activity event must
        // share the originating connector thread id so one query
        // reconstructs the chain. The neutral terminal (WorkflowStepCompleted)
        // and the DecisionMade both carry it.
        var inbound = MakeConnectorMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(MakeDispatchResponse());

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        emitted.ShouldNotBeEmpty();
        emitted.ShouldAllBe(e => e.CorrelationId == inbound.ThreadId);
    }
}
