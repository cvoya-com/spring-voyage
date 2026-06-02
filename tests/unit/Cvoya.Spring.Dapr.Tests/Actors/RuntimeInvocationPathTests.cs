// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="RuntimeInvocationPath"/>. The class encapsulates
/// the runtime-invocation pipeline extracted from <c>AgentActor</c> in
/// ADR-0039 task C1; these tests pin the lean and rich call shapes so the
/// later phases (UnitActor wiring in C2; directory-driven messaging
/// tools in D2) can extend the seam without regressing the contract.
/// </summary>
public class RuntimeInvocationPathTests
{
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IAgentDispatchCoordinator _dispatchCoordinator = Substitute.For<IAgentDispatchCoordinator>();

    private static Address MakeAgent(string slug) => Address.For(Address.AgentScheme, TestSlugIds.HexFor(slug));

    private static Address MakeUnit(string slug) => Address.For(Address.UnitScheme, TestSlugIds.HexFor(slug));

    private static Message MakeMessage(Address from, Address to, string? threadId = null)
        => new(
            Guid.NewGuid(),
            from,
            to,
            MessageType.Domain,
            threadId ?? Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { hello = "world" }),
            DateTimeOffset.UtcNow);

    private RuntimeInvocationPath MakePath(
        IAgentDispatchCoordinator? dispatchCoordinator = null)
    {
        return new RuntimeInvocationPath(
            _definitionProvider,
            dispatchCoordinator ?? _dispatchCoordinator);
    }

    private static MessageRouter MakeRouter()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        return Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory,
            NullMessageWriterScopeFactory.Create(),
            Substitute.For<Cvoya.Spring.Core.Lifecycle.ILifecycleStatusStore>());
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_DispatchesViaCoordinator()
    {
        var subject = MakeAgent("test-agent");
        var sender = MakeAgent("test-sender");
        var inbound = MakeMessage(sender, subject);
        var path = MakePath();

        await path.InvokeAsync(subject, inbound, TestContext.Current.CancellationToken);

        await _dispatchCoordinator.Received(1).RunDispatchAsync(
            agentId: subject.Path,
            message: inbound,
            context: Arg.Any<PromptAssemblyContext>(),
            emitActivity: Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            onDispatchExit: Arg.Any<Func<string, Task>>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_DetachesDispatchFromCallerCancellation()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var path = MakePath();

        using var requestCts = new CancellationTokenSource();

        await path.InvokeAsync(subject, inbound, requestCts.Token);

        await _dispatchCoordinator.Received(1).RunDispatchAsync(
            agentId: subject.Path,
            message: inbound,
            context: Arg.Any<PromptAssemblyContext>(),
            emitActivity: Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            onDispatchExit: Arg.Any<Func<string, Task>>(),
            cancellationToken: Arg.Is<CancellationToken>(ct => !ct.CanBeCanceled));
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_PullsAgentInstructionsFromDefinition()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        _definitionProvider.GetByIdAsync(subject.Path, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(subject.Path, "Test", "Be helpful.", null));
        var path = MakePath();

        PromptAssemblyContext? captured = null;
        await _dispatchCoordinator.RunDispatchAsync(
            Arg.Any<string>(),
            Arg.Any<Message>(),
            Arg.Do<PromptAssemblyContext>(ctx => captured = ctx),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>());

        await path.InvokeAsync(subject, inbound, TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.AgentInstructions.ShouldBe("Be helpful.");
    }

    /// <summary>
    /// Pins the #2670 invariant: the lean overload builds a minimal
    /// context that does not enumerate skill registries. The
    /// always-on platform-tool catalog rides Layer 1 via
    /// <see cref="IPlatformPromptProvider"/>, so the lean path needs
    /// no skill projection at all — every field other than
    /// <c>AgentInstructions</c> is left at its default (null).
    /// </summary>
    [Fact]
    public async Task InvokeAsync_LeanOverload_BuildsMinimalContextWithoutSkillProjection()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        _definitionProvider.GetByIdAsync(subject.Path, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: subject.Path,
                Name: "Test Agent",
                Instructions: "Be helpful.",
                Execution: null));
        var path = MakePath();

        PromptAssemblyContext? captured = null;
        await _dispatchCoordinator.RunDispatchAsync(
            Arg.Any<string>(),
            Arg.Any<Message>(),
            Arg.Do<PromptAssemblyContext>(ctx => captured = ctx),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>());

        await path.InvokeAsync(subject, inbound, TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.AgentInstructions.ShouldBe("Be helpful.");
        captured.Policies.ShouldBeNull();
        captured.SkillBundles.ShouldBeNull();
        captured.AgentSkillBundles.ShouldBeNull();
        captured.PendingAmendments.ShouldBeNull();
    }

    [Fact]
    public async Task InvokeAsync_RichOverload_ForwardsContextAndDelegatesUnchanged()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var path = MakePath();

        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "instructions",
            EffectiveMetadata: null,
            PendingAmendments: null);

        Func<ActivityEvent, CancellationToken, Task> emit = (_, _) => Task.CompletedTask;
        Func<string, Task> clear = _ => Task.CompletedTask;

        await path.InvokeAsync(subject, inbound, context, emit, clear, TestContext.Current.CancellationToken);

        await _dispatchCoordinator.Received(1).RunDispatchAsync(
            agentId: subject.Path,
            message: inbound,
            context: context,
            emitActivity: emit,
            onDispatchExit: clear,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_NoEmitDelegate_ForwardsNoOpToCoordinator()
    {
        // #2211: when no emitActivity delegate is supplied (existing
        // call shape preserved), the lean overload must still pass a
        // non-null no-op delegate to the dispatch coordinator so the
        // coordinator's signature is satisfied and any error events it
        // emits are silently dropped — the original behaviour.
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var path = MakePath();

        Func<ActivityEvent, CancellationToken, Task>? captured = null;
        await _dispatchCoordinator.RunDispatchAsync(
            Arg.Any<string>(),
            Arg.Any<Message>(),
            Arg.Any<PromptAssemblyContext>(),
            Arg.Do<Func<ActivityEvent, CancellationToken, Task>>(d => captured = d),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>());

        await path.InvokeAsync(subject, inbound, TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        // Invoking the no-op must complete synchronously without throwing.
        await captured!(
            new ActivityEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                subject,
                ActivityEventType.ErrorOccurred,
                ActivitySeverity.Error,
                "irrelevant",
                null,
                null),
            CancellationToken.None);
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_WithEmitDelegate_ForwardsSubjectRewritingDelegateToCoordinator()
    {
        // #2211: the lean overload must forward a caller-supplied
        // emitActivity delegate to the dispatch coordinator so that
        // ErrorOccurred events (e.g. credential-resolution failures)
        // surface in the caller's Activity feed instead of being
        // dropped by the no-op default.
        //
        // #2207: the coordinator still builds agent-shaped events for
        // the rich AgentActor path. The lean path adapts the event source
        // back to the actual subject so unit invocations surface in the
        // unit activity stream.
        var subject = MakeUnit("test-unit");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var path = MakePath();

        var publishedEvents = new List<ActivityEvent>();
        Func<ActivityEvent, CancellationToken, Task> emit = (activityEvent, _) =>
        {
            publishedEvents.Add(activityEvent);
            return Task.CompletedTask;
        };

        Func<ActivityEvent, CancellationToken, Task>? capturedEmit = null;
        await _dispatchCoordinator.RunDispatchAsync(
            Arg.Any<string>(),
            Arg.Any<Message>(),
            Arg.Any<PromptAssemblyContext>(),
            Arg.Do<Func<ActivityEvent, CancellationToken, Task>>(d => capturedEmit = d),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>());
        _dispatchCoordinator.ClearReceivedCalls();

        await path.InvokeAsync(
            subject,
            inbound,
            TestContext.Current.CancellationToken,
            emitActivity: emit);

        await _dispatchCoordinator.Received(1).RunDispatchAsync(
            agentId: subject.Path,
            message: inbound,
            context: Arg.Any<PromptAssemblyContext>(),
            emitActivity: Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            onDispatchExit: Arg.Any<Func<string, Task>>(),
            cancellationToken: Arg.Any<CancellationToken>());

        capturedEmit.ShouldNotBeNull();
        var coordinatorEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            MakeAgent("test-unit"),
            ActivityEventType.ErrorOccurred,
            ActivitySeverity.Error,
            "Dispatch failed: missing execution config",
            null,
            inbound.ThreadId);

        await capturedEmit!(coordinatorEvent, CancellationToken.None);

        publishedEvents.Count.ShouldBe(1);
        publishedEvents[0].Source.ShouldBe(subject);
        publishedEvents[0].EventType.ShouldBe(ActivityEventType.ErrorOccurred);
        publishedEvents[0].Summary.ShouldBe(coordinatorEvent.Summary);
        publishedEvents[0].CorrelationId.ShouldBe(inbound.ThreadId);
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_DispatcherThrows_PublishesErrorActivity()
    {
        var subject = MakeUnit("test-unit");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns<RuntimeOutcome>(_ => throw new InvalidOperationException("simulated dispatch failure"));

        var coordinator = new AgentDispatchCoordinator(
            dispatcher,
            Substitute.For<ILogger<AgentDispatchCoordinator>>());
        var path = MakePath(dispatchCoordinator: coordinator);
        var publishedEvents = new List<ActivityEvent>();

        await path.InvokeAsync(
            subject,
            inbound,
            TestContext.Current.CancellationToken,
            emitActivity: (activityEvent, _) =>
            {
                publishedEvents.Add(activityEvent);
                return Task.CompletedTask;
            });

        // Under ADR-0056 the coordinator emits the per-phase lifecycle
        // events before invoking the dispatcher, so a dispatcher that
        // throws still leaves the MessageDispatchedToRuntime + RuntimeStarted
        // rows on the stream — followed by the ErrorOccurred row that
        // surfaces the exception.
        var error = publishedEvents.Where(e => e.EventType == ActivityEventType.ErrorOccurred).ShouldHaveSingleItem();
        error.Source.ShouldBe(subject);
        error.Severity.ShouldBe(ActivitySeverity.Error);
        error.CorrelationId.ShouldBe(inbound.ThreadId);
        error.Summary.ShouldContain("simulated dispatch failure");
        error.Details.ShouldNotBeNull();
        error.Details.Value.GetProperty("error").GetString().ShouldBe("simulated dispatch failure");
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_SilentDispatch_PublishesRuntimeCompletedSilent()
    {
        // ADR-0056 §5: a silent runtime (clean exit, no tool calls)
        // surfaces as RuntimeCompletedSilent on the lean path's activity
        // stream. The legacy "Dispatch returned no response."
        // WorkflowStepCompleted shim is gone.
        var subject = MakeUnit("test-unit");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Silent());

        var coordinator = new AgentDispatchCoordinator(
            dispatcher,
            Substitute.For<ILogger<AgentDispatchCoordinator>>());
        var path = MakePath(dispatchCoordinator: coordinator);
        var publishedEvents = new List<ActivityEvent>();

        await path.InvokeAsync(
            subject,
            inbound,
            TestContext.Current.CancellationToken,
            emitActivity: (activityEvent, _) =>
            {
                publishedEvents.Add(activityEvent);
                return Task.CompletedTask;
            });

        publishedEvents.ShouldContain(e =>
            e.EventType == ActivityEventType.RuntimeCompletedSilent
            && e.Source == subject
            && e.CorrelationId == inbound.ThreadId);

        publishedEvents.ShouldNotContain(e =>
            e.EventType == ActivityEventType.WorkflowStepCompleted
            && (e.Summary ?? string.Empty).Contains("no response"));
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_ConnectorEvent_PublishesRoutingDecision()
    {
        // #2560: a connector event a unit processes through the lean path
        // (the UnitActor path) must leave a DecisionMade routing-outcome row
        // even when no agent is dispatched — the activity stream was
        // previously silent on that "no action" outcome.
        var subject = MakeUnit("triage-unit");
        var threadId = Guid.NewGuid().ToString();
        var connectorFrom = new Address(
            Address.ConnectorScheme,
            new Guid("00000000-0000-0000-0000-006769746875"));
        var inbound = new Message(
            Guid.NewGuid(),
            connectorFrom,
            subject,
            MessageType.Domain,
            threadId,
            JsonSerializer.SerializeToElement(new
            {
                source = "github",
                intent = "label_change",
                action = "unlabeled",
                issue = new { number = 2535 },
            }),
            DateTimeOffset.UtcNow);

        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns(RuntimeOutcomes.Success());

        var coordinator = new AgentDispatchCoordinator(
            dispatcher,
            Substitute.For<ILogger<AgentDispatchCoordinator>>());
        var path = MakePath(dispatchCoordinator: coordinator);
        var publishedEvents = new List<ActivityEvent>();

        await path.InvokeAsync(
            subject,
            inbound,
            TestContext.Current.CancellationToken,
            emitActivity: (activityEvent, _) =>
            {
                publishedEvents.Add(activityEvent);
                return Task.CompletedTask;
            });

        var decision = publishedEvents
            .Where(e => e.EventType == ActivityEventType.DecisionMade)
            .ShouldHaveSingleItem();
        decision.Source.ShouldBe(subject);
        decision.CorrelationId.ShouldBe(threadId);
        decision.Details.ShouldNotBeNull()
            .GetProperty("connectorEventType").GetString().ShouldBe("github.unlabeled");

        // Acceptance criterion 3: the whole trail shares the connector
        // thread id as correlation id — a dispatched agent's MessageArrived
        // (correlationId = message.ThreadId) joins the same chain.
        publishedEvents.ShouldAllBe(e => e.CorrelationId == threadId);
    }

    [Fact]
    public async Task InvokeAsync_RichOverload_DoesNotConsultDefinitionOrToolProvider()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var path = MakePath();

        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: null);

        await path.InvokeAsync(
            subject, inbound, context,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        await _definitionProvider.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
