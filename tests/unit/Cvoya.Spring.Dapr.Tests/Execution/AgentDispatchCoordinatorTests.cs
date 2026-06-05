// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentDispatchCoordinator"/>'s ADR-0056 §7
/// activity stream — the per-phase emissions
/// (<see cref="ActivityEventType.MessageDispatchedToRuntime"/>,
/// <see cref="ActivityEventType.RuntimeStarted"/>, the terminal
/// <see cref="ActivityEventType.RuntimeCompleted"/> /
/// <see cref="ActivityEventType.RuntimeFailed"/> /
/// <see cref="ActivityEventType.RuntimeCompletedSilent"/>, and the
/// diagnostic <see cref="ActivityEventType.RuntimeReasoning"/>) plus the
/// routing-decision activity (#2560) that connector-origin turns leave on
/// the stream.
/// </summary>
public class AgentDispatchCoordinatorTests
{
    private static readonly Address ConnectorFrom =
        new(Address.ConnectorScheme, new Guid("00000000-0000-0000-0000-006769746875"));

    private static readonly Address UnitTo =
        Address.For(Address.UnitScheme, TestSlugIds.HexFor("triage-unit"));

    private static AgentDispatchCoordinator MakeCoordinator(IExecutionDispatcher dispatcher)
        => new(
            dispatcher,
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

    // ------------------------------------------------------------------
    // ADR-0056 §7 lifecycle emissions
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunDispatchAsync_SuccessfulTurnWithToolCalls_EmitsExpectedPhaseSequence()
    {
        var inbound = MakeAgentMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Success(toolCallCount: 2));

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        // Phase order: dispatched-to-runtime → started → completed terminal.
        // No reasoning event because the outcome reports a null trace.
        emitted.Select(e => e.EventType).ShouldBe(new[]
        {
            ActivityEventType.MessageDispatchedToRuntime,
            ActivityEventType.RuntimeStarted,
            ActivityEventType.RuntimeCompleted,
        });

        var terminal = emitted[^1];
        terminal.Details.ShouldNotBeNull();
        terminal.Details!.Value.GetProperty("exitCode").GetInt32().ShouldBe(0);
        terminal.Details!.Value.GetProperty("toolCallCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task RunDispatchAsync_SilentRuntime_EmitsRuntimeCompletedSilentAndNotRuntimeCompleted()
    {
        // ADR-0056 §5: a clean exit with zero tool calls is the
        // compliance gap — surface it as RuntimeCompletedSilent so the
        // silence is diagnosable, never auto-wrap into a synthesised
        // message.
        var inbound = MakeAgentMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Silent("I would have said hello but I forgot the tool exists."));

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        emitted.ShouldContain(e => e.EventType == ActivityEventType.RuntimeCompletedSilent);
        emitted.ShouldNotContain(e => e.EventType == ActivityEventType.RuntimeCompleted);
        emitted.ShouldNotContain(e => e.EventType == ActivityEventType.RuntimeFailed);

        // The reasoning trace surfaces as its own event when present.
        emitted.ShouldContain(e => e.EventType == ActivityEventType.RuntimeReasoning);
    }

    [Fact]
    public async Task RunDispatchAsync_NonZeroExit_EmitsRuntimeFailedNotErrorOccurred()
    {
        var inbound = MakeAgentMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Failure(exitCode: 137));

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        var failed = emitted.Where(e => e.EventType == ActivityEventType.RuntimeFailed).ShouldHaveSingleItem();
        failed.Severity.ShouldBe(ActivitySeverity.Error);
        failed.Details.ShouldNotBeNull();
        failed.Details!.Value.GetProperty("exitCode").GetInt32().ShouldBe(137);

        // The legacy ErrorOccurred "Container exit code …" emission is gone.
        emitted.ShouldNotContain(e =>
            e.EventType == ActivityEventType.ErrorOccurred
            && (e.Summary ?? string.Empty).Contains("Container exit code"));
    }

    [Fact]
    public async Task RunDispatchAsync_NeverEmitsWorkflowStepCompletedDispatchResponseRecordedOnThread()
    {
        // Regression guard for the deleted synthesis row (ADR-0056 §9).
        var inbound = MakeAgentMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Success());

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        emitted.ShouldNotContain(e =>
            e.EventType == ActivityEventType.WorkflowStepCompleted
            && (e.Summary ?? string.Empty).Contains("Dispatch response recorded on thread"));
    }

    [Fact]
    public async Task RunDispatchAsync_ReasoningTrace_EmittedBeforeTerminal()
    {
        // Activity-emit order (load-bearing per the implementation brief):
        // reasoning lands before the terminal so consumers reading
        // top-to-bottom see reasoning first, terminal last.
        var inbound = MakeAgentMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Success(toolCallCount: 1, reasoningTrace: "thinking..."));

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        var reasoningIdx = emitted.FindIndex(e => e.EventType == ActivityEventType.RuntimeReasoning);
        var terminalIdx = emitted.FindIndex(e => e.EventType == ActivityEventType.RuntimeCompleted);
        reasoningIdx.ShouldBeGreaterThan(-1);
        terminalIdx.ShouldBeGreaterThan(reasoningIdx);
    }

    [Fact]
    public async Task RunDispatchAsync_ExactlyOneTerminal_PerTurn()
    {
        // Invariant: exactly one of RuntimeCompleted / RuntimeFailed /
        // RuntimeCompletedSilent per turn — never zero, never two.
        var inbound = MakeAgentMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Success());

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        var terminals = emitted.Count(e =>
            e.EventType is ActivityEventType.RuntimeCompleted
                or ActivityEventType.RuntimeFailed
                or ActivityEventType.RuntimeCompletedSilent);
        terminals.ShouldBe(1);
    }

    // ------------------------------------------------------------------
    // Connector routing-decision behaviour (#2560) — preserved under ADR-0056
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunDispatchAsync_ConnectorEvent_NoDelegation_EmitsDecisionMadeNoOp()
    {
        var inbound = MakeConnectorMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Success());

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
            .Returns(RuntimeOutcomes.Failure(exitCode: 1));

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
    public async Task RunDispatchAsync_ConnectorEvent_SilentTurn_StillEmitsDecisionMade()
    {
        // Under ADR-0056 a runtime that returns nothing observable is
        // surfaced as RuntimeCompletedSilent, not the legacy "null
        // response" branch. The connector routing-decision row must
        // still land so the stream isn't silent on the outcome.
        var inbound = MakeConnectorMessage(action: "labeled");
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Silent());

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
        var inbound = MakeAgentMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Success());

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        emitted.ShouldNotContain(e => e.EventType == ActivityEventType.DecisionMade);
    }

    [Fact]
    public async Task RunDispatchAsync_ConnectorEvent_AllEventsShareCorrelationId()
    {
        var inbound = MakeConnectorMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Success());

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        emitted.ShouldNotBeEmpty();
        emitted.ShouldAllBe(e => e.CorrelationId == inbound.ThreadId);
    }

    // ------------------------------------------------------------------
    // #3073: cost emission — the live producer the cost ledger + enforcer read
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunDispatchAsync_OutcomeWithCost_EmitsCostIncurredBeforeTerminal()
    {
        var inbound = MakeAgentMessage();
        var outcome = new RuntimeOutcome(
            ExitCode: 0,
            Duration: TimeSpan.FromMilliseconds(1234),
            ReasoningTrace: "done",
            Diagnostics: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [RuntimeOutcome.ToolCallCountKey] = 1,
                [RuntimeOutcome.CostUsdKey] = 0.05m,
                [RuntimeOutcome.InputTokensKey] = 1000,
                [RuntimeOutcome.OutputTokensKey] = 500,
                [RuntimeOutcome.ModelKey] = "claude-opus-4-8",
                ["unitId"] = "unit-hex",
                ["tenantId"] = "tenant-hex",
            });

        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(outcome);

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        var cost = emitted.Where(e => e.EventType == ActivityEventType.CostIncurred).ShouldHaveSingleItem();
        cost.Cost.ShouldBe(0.05m);
        cost.Details.ShouldNotBeNull();
        cost.Details!.Value.GetProperty("model").GetString().ShouldBe("claude-opus-4-8");
        cost.Details!.Value.GetProperty("inputTokens").GetInt32().ShouldBe(1000);
        cost.Details!.Value.GetProperty("outputTokens").GetInt32().ShouldBe(500);
        cost.Details!.Value.GetProperty("costSource").GetString().ShouldBe("Work");
        cost.Details!.Value.GetProperty("unitId").GetString().ShouldBe("unit-hex");

        // Cost lands before the terminal RuntimeCompleted so a top-to-bottom
        // reader sees the turn's spend with its completion.
        var costIndex = emitted.FindIndex(e => e.EventType == ActivityEventType.CostIncurred);
        var terminalIndex = emitted.FindIndex(e => e.EventType == ActivityEventType.RuntimeCompleted);
        costIndex.ShouldBeLessThan(terminalIndex);
    }

    [Fact]
    public async Task RunDispatchAsync_OutcomeWithoutCost_EmitsNoCostIncurred()
    {
        var inbound = MakeAgentMessage();
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Success());

        var emitted = await RunAsync(MakeCoordinator(dispatcher), inbound);

        emitted.ShouldNotContain(e => e.EventType == ActivityEventType.CostIncurred);
    }
}
