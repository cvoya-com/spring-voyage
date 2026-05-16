// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Skills;
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
/// later phases (UnitActor wiring in C2; directory-driven orchestration
/// tools in D2) can extend the seam without regressing the contract.
/// </summary>
public class RuntimeInvocationPathTests
{
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IOrchestrationToolProvider _toolProvider = Substitute.For<IOrchestrationToolProvider>();
    private readonly IAgentDispatchCoordinator _dispatchCoordinator = Substitute.For<IAgentDispatchCoordinator>();
    private readonly ILogger<RuntimeInvocationPath> _logger = Substitute.For<ILogger<RuntimeInvocationPath>>();

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
        IEnumerable<ISkillRegistry>? skillRegistries = null,
        IAgentDispatchCoordinator? dispatchCoordinator = null)
    {
        _toolProvider
            .GetOrchestrationTools(Arg.Any<Address>(), Arg.Any<Guid>())
            .Returns(Array.Empty<OrchestrationToolDescriptor>());

        return new RuntimeInvocationPath(
            _definitionProvider,
            skillRegistries ?? Array.Empty<ISkillRegistry>(),
            _toolProvider,
            dispatchCoordinator ?? _dispatchCoordinator,
            _logger);
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
            NullMessageWriterScopeFactory.Create());
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

    [Fact]
    public async Task InvokeAsync_LeanOverload_AssemblesSkillsFromRegistries()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var registry = Substitute.For<ISkillRegistry>();
        registry.Name.Returns("github");
        registry.GetToolDefinitions().Returns([
            new ToolDefinition("github.comment", "Comment", JsonSerializer.SerializeToElement(new { }))
        ]);
        var path = MakePath([registry]);

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
        captured!.Skills.ShouldNotBeNull();
        captured.Skills!.Count.ShouldBe(1);
        captured.Skills[0].Name.ShouldBe("github");
        captured.Skills[0].Tools.Count.ShouldBe(1);
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_ResolvesOrchestrationToolsForThread()
    {
        var subject = MakeAgent("test-agent");
        var threadId = Guid.NewGuid();
        var inbound = MakeMessage(MakeAgent("test-sender"), subject, threadId.ToString());
        var path = MakePath();

        await path.InvokeAsync(subject, inbound, TestContext.Current.CancellationToken);

        _toolProvider.Received(1).GetOrchestrationTools(subject, threadId);
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_NonGuidThreadIdFallsBackToEmpty()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject, "not-a-guid");
        var path = MakePath();

        await path.InvokeAsync(subject, inbound, TestContext.Current.CancellationToken);

        _toolProvider.Received(1).GetOrchestrationTools(subject, Guid.Empty);
    }

    [Fact]
    public async Task InvokeAsync_RichOverload_ForwardsContextAndDelegatesUnchanged()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var path = MakePath();

        var context = new PromptAssemblyContext(
            Policies: null,
            Skills: null,
            PriorMessages: [inbound],
            LastCheckpoint: null,
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
            .Returns<Message?>(_ => throw new InvalidOperationException("simulated dispatch failure"));

        var coordinator = new AgentDispatchCoordinator(
            dispatcher,
            MakeRouter(),
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

        publishedEvents.Count.ShouldBe(1);
        var published = publishedEvents[0];
        published.Source.ShouldBe(subject);
        published.EventType.ShouldBe(ActivityEventType.ErrorOccurred);
        published.Severity.ShouldBe(ActivitySeverity.Error);
        published.CorrelationId.ShouldBe(inbound.ThreadId);
        published.Summary.ShouldContain("simulated dispatch failure");
        published.Details.ShouldNotBeNull();
        published.Details.Value.GetProperty("error").GetString().ShouldBe("simulated dispatch failure");
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_DispatcherReturnsNull_PublishesNoResponseActivity()
    {
        var subject = MakeUnit("test-unit");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var dispatcher = Substitute.For<IExecutionDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<Message>(), Arg.Any<PromptAssemblyContext?>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var coordinator = new AgentDispatchCoordinator(
            dispatcher,
            MakeRouter(),
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

        publishedEvents.Count.ShouldBe(1);
        var published = publishedEvents[0];
        published.Source.ShouldBe(subject);
        published.EventType.ShouldBe(ActivityEventType.WorkflowStepCompleted);
        published.Severity.ShouldBe(ActivitySeverity.Info);
        published.CorrelationId.ShouldBe(inbound.ThreadId);
        published.Summary.ShouldContain("no response");
        published.Details.ShouldNotBeNull();
        published.Details.Value.GetProperty("reason").GetString().ShouldBe("dispatch returned no response");
    }

    [Fact]
    public async Task InvokeAsync_RichOverload_DoesNotConsultDefinitionOrToolProvider()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var path = MakePath();

        var context = new PromptAssemblyContext(
            Policies: null,
            Skills: null,
            PriorMessages: Array.Empty<Message>(),
            LastCheckpoint: null,
            AgentInstructions: null);

        await path.InvokeAsync(
            subject, inbound, context,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        await _definitionProvider.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _toolProvider.DidNotReceive().GetOrchestrationTools(Arg.Any<Address>(), Arg.Any<Guid>());
    }
}
