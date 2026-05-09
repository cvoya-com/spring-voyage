// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

public class OrchestrationToolHandlersTests
{
    private static readonly Guid UnitId = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid ChildAgentId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherChildAgentId = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid NonChildAgentId = new("aaaaaaaa-0000-0000-0000-000000000099");

    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IAgentProxyResolver _agentProxyResolver = Substitute.For<IAgentProxyResolver>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly ILogger<OrchestrationToolHandlers> _logger =
        Substitute.For<ILogger<OrchestrationToolHandlers>>();

    private readonly Dictionary<string, Address[]> _members = new();
    private readonly Dictionary<string, IAgent> _agents = new();
    private readonly List<ActivityEvent> _publishedEvents = [];

    public OrchestrationToolHandlersTests()
    {
        _actorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), nameof(UnitActor))
            .Returns(ci =>
            {
                var actorId = ci.ArgAt<ActorId>(0).GetId();
                var actor = Substitute.For<IUnitActor>();
                var members = _members.TryGetValue(actorId, out var m) ? m : Array.Empty<Address>();
                actor.GetMembersAsync(Arg.Any<CancellationToken>()).Returns(members);
                return actor;
            });

        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci =>
            {
                var scheme = ci.ArgAt<string>(0);
                var actorId = ci.ArgAt<string>(1);
                return _agents.TryGetValue($"{scheme}:{actorId}", out var agent)
                    ? agent
                    : null;
            });

        _activityEventBus
            .PublishAsync(Arg.Do<ActivityEvent>(evt => _publishedEvents.Add(evt)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task HandleListChildren_NonUnitCaller_ThrowsCallerIsNotUnit()
    {
        var handlers = CreateHandlers();
        var caller = Agent(NonChildAgentId);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleListChildrenAsync(caller, Guid.NewGuid(), TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationCallerIsNotUnit);
    }

    [Fact]
    public async Task HandleListChildren_DoesNotEmitEvent()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var child = Agent(ChildAgentId);
        RegisterMembers(caller, child);

        var result = await handlers.HandleListChildrenAsync(
            caller,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        result.ShouldBe([child]);
        await AssertNoDecisionPublishedAsync();
    }

    [Fact]
    public async Task HandleDelegateToChild_NonUnitCaller_ThrowsCallerIsNotUnit()
    {
        var handlers = CreateHandlers();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleDelegateToChildAsync(
                Agent(NonChildAgentId),
                Agent(ChildAgentId),
                CreateMessage(),
                null,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationCallerIsNotUnit);
    }

    [Fact]
    public async Task HandleDelegateToChild_TargetNotChild_ThrowsTargetNotChild()
    {
        var handlers = CreateHandlers();
        RegisterMembers(Unit(), Agent(ChildAgentId));

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleDelegateToChildAsync(
                Unit(),
                Agent(NonChildAgentId),
                CreateMessage(),
                null,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationTargetNotChild);
    }

    [Fact]
    public async Task HandleDelegateToChild_SelfTarget_ThrowsSelfDelegation()
    {
        var handlers = CreateHandlers();
        RegisterMembers(Unit(), Agent(ChildAgentId));

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleDelegateToChildAsync(
                Unit(),
                Unit(),
                CreateMessage(),
                null,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationSelfDelegation);
    }

    [Fact]
    public async Task HandleDelegateToChild_DepthBudgetExhausted_ThrowsDepthExceeded()
    {
        var depthCounter = new OrchestrationDepthCounter(maxDepth: 1);
        var handlers = CreateHandlers(depthCounter);
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var threadId = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = Substitute.For<IAgent>();

        RegisterMembers(caller, target);
        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                started.TrySetResult();
                return release.Task;
            });

        var firstCall = handlers.HandleDelegateToChildAsync(
            caller,
            target,
            CreateMessage(),
            null,
            threadId,
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleDelegateToChildAsync(
                caller,
                target,
                CreateMessage(),
                null,
                threadId,
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationDepthExceeded);

        release.SetResult(null);
        await firstCall;
    }

    [Fact]
    public async Task HandleDelegateToChild_HappyPath_EmitsRouted()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var response = CreateResponse(target, caller);
        var message = CreateMessage();
        var threadId = Guid.NewGuid();
        Message? delivered = null;
        var agent = Substitute.For<IAgent>();

        RegisterMembers(caller, target);
        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Do<Message>(m => delivered = m), Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await handlers.HandleDelegateToChildAsync(
            caller,
            target,
            message,
            null,
            threadId,
            TestContext.Current.CancellationToken);

        result.ShouldBe(response);
        delivered.ShouldNotBeNull();
        delivered!.From.ShouldBe(caller);
        delivered.To.ShouldBe(target);

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(evt => IsDecisionEvent(
                evt,
                OrchestrationDecisionKind.Delegate,
                OrchestrationDecisionStatus.Routed)),
            Arg.Any<CancellationToken>());

        var decision = ReadSingleDecision(caller);
        decision.TenantId.ShouldBe(Guid.Empty);
        decision.UnitAddress.ShouldBe(caller);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Delegate);
        decision.Targets.ShouldBe([target]);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Routed);
        decision.ResultMessageIds.ShouldBe([response.Id]);
        decision.Reason.ShouldBeNull();
        decision.Metadata.ShouldBeNull();
        decision.DecisionId.ShouldNotBe(Guid.Empty);
        decision.CreatedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task HandleDelegateToChild_GitHubIssuePayload_EmitsIssueNumberMetadata()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var agent = Substitute.For<IAgent>();

        RegisterMembers(caller, target);
        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(CreateResponse(target, caller));

        await handlers.HandleDelegateToChildAsync(
            caller,
            target,
            CreateGitHubIssueMessage(number: 42),
            null,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        var decision = ReadSingleDecision(caller);
        decision.Metadata.ShouldNotBeNull();
        var metadata = decision.Metadata!.Value;
        metadata.GetProperty("issue").GetProperty("number").GetInt32()
            .ShouldBe(42);
        metadata.GetProperty("issue").EnumerateObject()
            .Select(p => p.Name)
            .ShouldBe(["number"]);
    }

    [Fact]
    public async Task HandleDelegateToChild_NonGitHubIssuePayload_DoesNotEmitIssueMetadata()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var agent = Substitute.For<IAgent>();

        RegisterMembers(caller, target);
        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(CreateResponse(target, caller));

        await handlers.HandleDelegateToChildAsync(
            caller,
            target,
            CreateNonGitHubIssueMessage(number: 42),
            null,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        var decision = ReadSingleDecision(caller);
        decision.Metadata.ShouldBeNull();
    }

    [Fact]
    public async Task HandleDelegateToChild_Exception_EmitsFailed()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var message = CreateMessage();
        var threadId = Guid.NewGuid();
        const string reason = "target owns the work";

        RegisterMembers(caller, target);
        _agentProxyResolver.Resolve(target.Scheme, target.Id.ToString("N"))
            .Throws(new InvalidOperationException("resolver failed"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            handlers.HandleDelegateToChildAsync(
                caller,
                target,
                message,
                reason,
                threadId,
                TestContext.Current.CancellationToken));

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(evt => IsDecisionEvent(
                evt,
                OrchestrationDecisionKind.Delegate,
                OrchestrationDecisionStatus.Failed)),
            Arg.Any<CancellationToken>());

        var decision = ReadSingleDecision(caller);
        decision.UnitAddress.ShouldBe(caller);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Delegate);
        decision.Targets.ShouldBe([target]);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Failed);
        decision.ResultMessageIds.ShouldBeEmpty();
        decision.Reason.ShouldBe(reason);
    }

    [Fact]
    public async Task HandleDelegateToChild_HappyPath_WithReason_RoundTrips()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        const string reason = "specialized child owns this work";
        var agent = Substitute.For<IAgent>();

        RegisterMembers(caller, target);
        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(CreateResponse(target, caller));

        await handlers.HandleDelegateToChildAsync(
            caller,
            target,
            CreateMessage(),
            reason,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains(reason)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task HandleFanoutToChildren_NonUnitCaller_ThrowsCallerIsNotUnit()
    {
        var handlers = CreateHandlers();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleFanoutToChildrenAsync(
                Agent(NonChildAgentId),
                [Agent(ChildAgentId)],
                CreateMessage(),
                null,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationCallerIsNotUnit);
    }

    [Fact]
    public async Task HandleFanoutToChildren_TargetNotChild_ThrowsTargetNotChild()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        RegisterMembers(caller, Agent(ChildAgentId));

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleFanoutToChildrenAsync(
                caller,
                [Agent(NonChildAgentId)],
                CreateMessage(),
                null,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationTargetNotChild);
    }

    [Fact]
    public async Task HandleFanoutToChildren_HappyPath_EmitsRouted()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var targetOne = Agent(ChildAgentId);
        var targetTwo = Agent(OtherChildAgentId);
        var responseOne = CreateResponse(targetOne, caller);
        var responseTwo = CreateResponse(targetTwo, caller);
        var message = CreateMessage();
        var threadId = Guid.NewGuid();
        const string reason = "ask both children";
        var agentOne = Substitute.For<IAgent>();
        var agentTwo = Substitute.For<IAgent>();

        RegisterMembers(caller, targetOne, targetTwo);
        RegisterAgent(targetOne, agentOne);
        RegisterAgent(targetTwo, agentTwo);
        agentOne.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(responseOne);
        agentTwo.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(responseTwo);

        var results = await handlers.HandleFanoutToChildrenAsync(
            caller,
            [targetOne, targetTwo],
            message,
            reason,
            threadId,
            TestContext.Current.CancellationToken);

        results.Length.ShouldBe(2);
        results[0].Target.ShouldBe(targetOne);
        results[0].Response.ShouldBe(responseOne);
        results[0].Error.ShouldBeNull();
        results[1].Target.ShouldBe(targetTwo);
        results[1].Response.ShouldBe(responseTwo);
        results[1].Error.ShouldBeNull();

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(evt => IsDecisionEvent(
                evt,
                OrchestrationDecisionKind.Fanout,
                OrchestrationDecisionStatus.Routed)),
            Arg.Any<CancellationToken>());

        var decision = ReadSingleDecision(caller);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Fanout);
        decision.Targets.ShouldBe([targetOne, targetTwo]);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Routed);
        decision.ResultMessageIds.ShouldBe([responseOne.Id, responseTwo.Id]);
        decision.Reason.ShouldBe(reason);
    }

    [Fact]
    public async Task HandleFanoutToChildren_PartialFailure_EmitsFailed()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var targetOne = Agent(ChildAgentId);
        var targetTwo = Agent(OtherChildAgentId);
        var response = CreateResponse(targetOne, caller);
        var message = CreateMessage();
        var threadId = Guid.NewGuid();
        var successfulAgent = Substitute.For<IAgent>();
        var failingAgent = Substitute.For<IAgent>();

        RegisterMembers(caller, targetOne, targetTwo);
        RegisterAgent(targetOne, successfulAgent);
        RegisterAgent(targetTwo, failingAgent);
        successfulAgent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(response);
        failingAgent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("child failed"));

        var results = await handlers.HandleFanoutToChildrenAsync(
            caller,
            [targetOne, targetTwo],
            message,
            null,
            threadId,
            TestContext.Current.CancellationToken);

        results.Length.ShouldBe(2);
        results[0].Target.ShouldBe(targetOne);
        results[0].Response.ShouldBe(response);
        results[0].Error.ShouldBeNull();
        results[1].Target.ShouldBe(targetTwo);
        results[1].Response.ShouldBeNull();
        results[1].Error.ShouldBeOfType<InvalidOperationException>();

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(evt => IsDecisionEvent(
                evt,
                OrchestrationDecisionKind.Fanout,
                OrchestrationDecisionStatus.Failed)),
            Arg.Any<CancellationToken>());

        var decision = ReadSingleDecision(caller);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Fanout);
        decision.Targets.ShouldBe([targetOne, targetTwo]);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Failed);
        decision.ResultMessageIds.ShouldBe([response.Id]);
    }

    [Fact]
    public async Task HandleInspectChild_NonUnitCaller_ThrowsCallerIsNotUnit()
    {
        var handlers = CreateHandlers();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleInspectChildAsync(
                Agent(NonChildAgentId),
                Agent(ChildAgentId),
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationCallerIsNotUnit);
    }

    [Fact]
    public async Task HandleInspectChild_TargetNotChild_ThrowsTargetNotChild()
    {
        var handlers = CreateHandlers();
        RegisterMembers(Unit(), Agent(ChildAgentId));

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleInspectChildAsync(
                Unit(),
                Agent(NonChildAgentId),
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationTargetNotChild);
    }

    [Fact]
    public async Task HandleInspectChild_DoesNotEmitEvent()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        RegisterMembers(caller, target);

        var meta = await handlers.HandleInspectChildAsync(
            caller,
            target,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        meta["scheme"].ShouldBe(Address.AgentScheme);
        meta["id"].ShouldBe(ChildAgentId);
        await AssertNoDecisionPublishedAsync();
    }

    [Fact]
    public async Task HandleQueryChildStatus_NonUnitCaller_ThrowsCallerIsNotUnit()
    {
        var handlers = CreateHandlers();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleQueryChildStatusAsync(
                Agent(NonChildAgentId),
                Agent(ChildAgentId),
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationCallerIsNotUnit);
    }

    [Fact]
    public async Task HandleQueryChildStatus_TargetNotChild_ThrowsTargetNotChild()
    {
        var handlers = CreateHandlers();
        RegisterMembers(Unit(), Agent(ChildAgentId));

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleQueryChildStatusAsync(
                Unit(),
                Agent(NonChildAgentId),
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationTargetNotChild);
    }

    [Fact]
    public async Task HandleQueryChildStatus_DoesNotEmitEvent()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        RegisterMembers(caller, target);

        var status = await handlers.HandleQueryChildStatusAsync(
            caller,
            target,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        status.ShouldBe("unknown");
        await AssertNoDecisionPublishedAsync();
    }

    private OrchestrationToolHandlers CreateHandlers(OrchestrationDepthCounter? depthCounter = null) =>
        new(
            _actorProxyFactory,
            _agentProxyResolver,
            depthCounter ?? new OrchestrationDepthCounter(),
            _logger,
            _activityEventBus);

    private async Task AssertNoDecisionPublishedAsync()
    {
        _publishedEvents.ShouldBeEmpty();
        await _activityEventBus.DidNotReceiveWithAnyArgs().PublishAsync(default!, default);
    }

    private OrchestrationDecision ReadSingleDecision(Address caller)
    {
        _publishedEvents.Count.ShouldBe(1);
        var activityEvent = _publishedEvents.Single();

        activityEvent.Source.ShouldBe(caller);
        activityEvent.EventType.ShouldBe(ActivityEventType.DecisionMade);
        activityEvent.Details.ShouldNotBeNull();
        activityEvent.CorrelationId.ShouldNotBeNull();

        var decision = JsonSerializer.Deserialize<OrchestrationDecision>(
            activityEvent.Details!.Value.GetRawText());

        decision.ShouldNotBeNull();
        return decision!;
    }

    private static bool IsDecisionEvent(
        ActivityEvent activityEvent,
        OrchestrationDecisionKind kind,
        OrchestrationDecisionStatus status)
    {
        if (activityEvent.EventType != ActivityEventType.DecisionMade ||
            activityEvent.Details is null)
        {
            return false;
        }

        var decision = JsonSerializer.Deserialize<OrchestrationDecision>(
            activityEvent.Details.Value.GetRawText());

        return decision is not null &&
            decision.Kind == kind &&
            decision.Status == status;
    }

    private void RegisterMembers(Address unit, params Address[] members) =>
        _members[unit.Id.ToString("N")] = members;

    private void RegisterAgent(Address address, IAgent agent) =>
        _agents[$"{address.Scheme}:{address.Id:N}"] = agent;

    private static Address Unit() => new(Address.UnitScheme, UnitId);

    private static Address Agent(Guid id) => new(Address.AgentScheme, id);

    private static Message CreateMessage() =>
        new(
            Guid.NewGuid(),
            new Address(Address.UnitScheme, Guid.NewGuid()),
            new Address(Address.AgentScheme, Guid.NewGuid()),
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Content = "work" }),
            DateTimeOffset.UtcNow);

    private static Message CreateGitHubIssueMessage(int number) =>
        new(
            Guid.NewGuid(),
            new Address(Address.UnitScheme, Guid.NewGuid()),
            new Address(Address.AgentScheme, Guid.NewGuid()),
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new
            {
                source = "github",
                issue = new
                {
                    number,
                    title = "Fix label roundtrip",
                    body = "body should not be copied into decision metadata",
                },
            }),
            DateTimeOffset.UtcNow);

    private static Message CreateNonGitHubIssueMessage(int number) =>
        new(
            Guid.NewGuid(),
            new Address(Address.UnitScheme, Guid.NewGuid()),
            new Address(Address.AgentScheme, Guid.NewGuid()),
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new
            {
                source = "jira",
                issue = new
                {
                    number,
                    title = "Different upstream",
                },
            }),
            DateTimeOffset.UtcNow);

    private static Message CreateResponse(Address from, Address to) =>
        new(
            Guid.NewGuid(),
            from,
            to,
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Content = "done" }),
            DateTimeOffset.UtcNow);
}