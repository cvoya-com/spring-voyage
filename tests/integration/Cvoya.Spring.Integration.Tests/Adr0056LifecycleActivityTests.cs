// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Acceptance test for the umbrella issue #2658 / ADR-0056 §7. A human →
/// agent turn produces, in order:
/// <list type="number">
///   <item><description><c>MessageArrived</c> for the inbound envelope.</description></item>
///   <item><description><c>ThreadStarted</c> (existing).</description></item>
///   <item><description><c>MessageDispatchedToRuntime</c>.</description></item>
///   <item><description><c>RuntimeStarted</c>.</description></item>
///   <item><description>Exactly one terminal: <c>RuntimeCompleted</c>,
///   <c>RuntimeFailed</c>, or <c>RuntimeCompletedSilent</c>.</description></item>
/// </list>
/// And critically: NO <c>WorkflowStepCompleted "Dispatch response recorded
/// on thread."</c> activity, anywhere.
/// </summary>
public class Adr0056LifecycleActivityTests
{
    private static Message MakeInbound(Address sender, Address recipient, string threadId)
        => new(
            Guid.NewGuid(),
            sender,
            recipient,
            MessageType.Domain,
            threadId,
            JsonSerializer.SerializeToElement("Hello"),
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task HumanToAgentHelloTurn_ToolInvokedRuntime_EmitsExpectedLifecycleSequence()
    {
        var agentId = Guid.NewGuid().ToString("N");
        var humanId = Guid.NewGuid();
        var threadId = Guid.NewGuid().ToString("D");
        var inbound = MakeInbound(
            new Address(Address.HumanScheme, humanId),
            Address.For(Address.AgentScheme, agentId),
            threadId);

        var harness = ActorTestHost.CreateAgentActorWithHarness(actorId: agentId);
        var published = new List<ActivityEvent>();
        harness.ActivityEventBus
            .PublishAsync(Arg.Do<ActivityEvent>(published.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // A runtime that invoked at least one tool call — terminal is
        // RuntimeCompleted (NOT RuntimeCompletedSilent).
        harness.ExecutionDispatcher
            .DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(new RuntimeOutcome(
                ExitCode: 0,
                Duration: TimeSpan.FromMilliseconds(50),
                ReasoningTrace: null,
                Diagnostics: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [RuntimeOutcome.ToolCallCountKey] = 1,
                }));

        await harness.Actor.ReceiveAsync(inbound, TestContext.Current.CancellationToken);
        if (harness.Actor.PendingDispatchTask is not null)
        {
            await harness.Actor.PendingDispatchTask;
        }

        // The required per-phase events must all land.
        published.ShouldContain(e => e.EventType == ActivityEventType.MessageArrived);
        published.ShouldContain(e => e.EventType == ActivityEventType.ThreadStarted);
        published.ShouldContain(e => e.EventType == ActivityEventType.MessageDispatchedToRuntime);
        published.ShouldContain(e => e.EventType == ActivityEventType.RuntimeStarted);
        published.ShouldContain(e => e.EventType == ActivityEventType.RuntimeCompleted);

        // Order invariant: arrived → dispatched-to-runtime → started → terminal.
        var arrivedIdx = published.FindIndex(e => e.EventType == ActivityEventType.MessageArrived);
        var dispatchedIdx = published.FindIndex(e => e.EventType == ActivityEventType.MessageDispatchedToRuntime);
        var startedIdx = published.FindIndex(e => e.EventType == ActivityEventType.RuntimeStarted);
        var terminalIdx = published.FindIndex(e => e.EventType == ActivityEventType.RuntimeCompleted);
        arrivedIdx.ShouldBeLessThan(dispatchedIdx);
        dispatchedIdx.ShouldBeLessThan(startedIdx);
        startedIdx.ShouldBeLessThan(terminalIdx);

        // Compliance gap absent on this path.
        published.ShouldNotContain(e => e.EventType == ActivityEventType.RuntimeCompletedSilent);
        published.ShouldNotContain(e => e.EventType == ActivityEventType.RuntimeFailed);

        // The deleted synthesis emission MUST NOT appear (ADR-0056 §9).
        published.ShouldNotContain(e =>
            e.EventType == ActivityEventType.WorkflowStepCompleted
            && (e.Summary ?? string.Empty).Contains("Dispatch response recorded on thread"));
    }

    [Fact]
    public async Task HumanToAgentHelloTurn_SilentRuntime_EmitsRuntimeCompletedSilentAndNoSynthesisedMessage()
    {
        var agentId = Guid.NewGuid().ToString("N");
        var humanId = Guid.NewGuid();
        var threadId = Guid.NewGuid().ToString("D");
        var inbound = MakeInbound(
            new Address(Address.HumanScheme, humanId),
            Address.For(Address.AgentScheme, agentId),
            threadId);

        var harness = ActorTestHost.CreateAgentActorWithHarness(actorId: agentId);
        var published = new List<ActivityEvent>();
        harness.ActivityEventBus
            .PublishAsync(Arg.Do<ActivityEvent>(published.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // A runtime that said "hello" on stdout but invoked no tool
        // calls — terminal is RuntimeCompletedSilent (ADR-0056 §5).
        harness.ExecutionDispatcher
            .DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(new RuntimeOutcome(
                ExitCode: 0,
                Duration: TimeSpan.FromMilliseconds(50),
                ReasoningTrace: "Hello! How can I help you today?",
                Diagnostics: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [RuntimeOutcome.ToolCallCountKey] = 0,
                }));

        await harness.Actor.ReceiveAsync(inbound, TestContext.Current.CancellationToken);
        if (harness.Actor.PendingDispatchTask is not null)
        {
            await harness.Actor.PendingDispatchTask;
        }

        published.ShouldContain(e => e.EventType == ActivityEventType.RuntimeCompletedSilent);

        // The reasoning trace surfaces as its own diagnostic event — never
        // routed as a message.
        published.ShouldContain(e => e.EventType == ActivityEventType.RuntimeReasoning);

        // The deleted synthesis emission is gone on every path.
        published.ShouldNotContain(e =>
            e.EventType == ActivityEventType.WorkflowStepCompleted
            && (e.Summary ?? string.Empty).Contains("Dispatch response recorded on thread"));
    }
}
