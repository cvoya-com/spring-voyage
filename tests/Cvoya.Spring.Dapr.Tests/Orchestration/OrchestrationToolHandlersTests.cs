// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
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
    private readonly ILogger<OrchestrationToolHandlers> _logger =
        Substitute.For<ILogger<OrchestrationToolHandlers>>();

    private readonly Dictionary<string, Address[]> _members = new();
    private readonly Dictionary<string, IAgent> _agents = new();

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
    public async Task HandleListChildren_HappyPath_ReturnsMembers()
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
    public async Task HandleDelegateToChild_HappyPath_ReturnsChildResponse()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var response = CreateResponse(target, caller);
        Message? delivered = null;
        var agent = Substitute.For<IAgent>();

        RegisterMembers(caller, target);
        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Do<Message>(m => delivered = m), Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await handlers.HandleDelegateToChildAsync(
            caller,
            target,
            CreateMessage(),
            null,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        result.ShouldBe(response);
        delivered.ShouldNotBeNull();
        delivered!.From.ShouldBe(caller);
        delivered.To.ShouldBe(target);
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
    public async Task HandleFanoutToChildren_OneTargetFails_OtherSucceeds()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var targetOne = Agent(ChildAgentId);
        var targetTwo = Agent(OtherChildAgentId);
        var response = CreateResponse(targetOne, caller);
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
            CreateMessage(),
            null,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        results.Length.ShouldBe(2);
        results[0].Target.ShouldBe(targetOne);
        results[0].Response.ShouldBe(response);
        results[0].Error.ShouldBeNull();
        results[1].Target.ShouldBe(targetTwo);
        results[1].Response.ShouldBeNull();
        results[1].Error.ShouldBeOfType<InvalidOperationException>();
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
    public async Task HandleInspectChild_HappyPath_ReturnsMeta()
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
    public async Task HandleQueryChildStatus_HappyPath_ReturnsUnknown()
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
    }

    private OrchestrationToolHandlers CreateHandlers(OrchestrationDepthCounter? depthCounter = null) =>
        new(
            _actorProxyFactory,
            _agentProxyResolver,
            depthCounter ?? new OrchestrationDepthCounter(),
            _logger);

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