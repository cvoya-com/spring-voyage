// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

public class OrchestrationToolHandlersTests
{
    private static readonly Guid UnitId = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid TenantId = OssTenantIds.Default;
    private static readonly Guid OtherTenantId = new("ffffffff-0000-0000-0000-000000000099");
    private static readonly Guid ChildAgentId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherChildAgentId = new("aaaaaaaa-0000-0000-0000-000000000002");

    private readonly IAgentProxyResolver _agentProxyResolver = Substitute.For<IAgentProxyResolver>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IOrchestrationTenantResolver _tenantResolver = Substitute.For<IOrchestrationTenantResolver>();
    private readonly ILogger<OrchestrationToolHandlers> _logger =
        Substitute.For<ILogger<OrchestrationToolHandlers>>();

    private readonly Dictionary<string, IAgent> _agents = new();
    private readonly List<ActivityEvent> _publishedEvents = [];

    public OrchestrationToolHandlersTests()
    {
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

        // Default resolver: every address belongs to the OSS default tenant.
        _tenantResolver.GetTenantForAddressAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(TenantId);
    }

    [Fact]
    public async Task HandleDelegateTo_AnyAddressableTarget_RoutesSuccessfully()
    {
        // ADR-0039 §3 (2026-05-19 amendment): the platform does not gate on
        // membership. Delegating to any addressable target in the same tenant
        // succeeds when the target resolves to an agent proxy.
        var handlers = CreateHandlers();
        var caller = Agent(new Guid("dddddddd-0000-0000-0000-000000000099"));
        var target = Agent(ChildAgentId);
        var response = CreateResponse(target, caller);
        var agent = Substitute.For<IAgent>();

        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await handlers.HandleDelegateToAsync(
            caller,
            TenantId,
            target,
            CreateMessage(),
            null,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        result.ShouldBe(response);
    }

    [Fact]
    public async Task HandleDelegateTo_SelfTarget_ThrowsSelfDelegation()
    {
        var handlers = CreateHandlers();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleDelegateToAsync(
                Unit(),
                TenantId,
                Unit(),
                CreateMessage(),
                null,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationSelfDelegation);
    }

    [Fact]
    public async Task HandleDelegateTo_CallerTenantMismatch_ThrowsCrossTenant()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);

        // Caller belongs to OtherTenantId, token claims TenantId.
        _tenantResolver.GetTenantForAddressAsync(caller, Arg.Any<CancellationToken>())
            .Returns(OtherTenantId);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleDelegateToAsync(
                caller,
                TenantId,
                target,
                CreateMessage(),
                null,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationCrossTenant);
    }

    [Fact]
    public async Task HandleDelegateTo_TargetTenantMismatch_ThrowsCrossTenant()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);

        // Caller resolves to TenantId; target resolves to OtherTenantId.
        _tenantResolver.GetTenantForAddressAsync(caller, Arg.Any<CancellationToken>())
            .Returns(TenantId);
        _tenantResolver.GetTenantForAddressAsync(target, Arg.Any<CancellationToken>())
            .Returns(OtherTenantId);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleDelegateToAsync(
                caller,
                TenantId,
                target,
                CreateMessage(),
                null,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationCrossTenant);
    }

    [Fact]
    public async Task HandleDelegateTo_DepthBudgetExhausted_ThrowsDepthExceeded()
    {
        var depthCounter = new OrchestrationDepthCounter(maxDepth: 1);
        var handlers = CreateHandlers(depthCounter);
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var threadId = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = Substitute.For<IAgent>();

        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                started.TrySetResult();
                return release.Task;
            });

        var firstCall = handlers.HandleDelegateToAsync(
            caller,
            TenantId,
            target,
            CreateMessage(),
            null,
            threadId,
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleDelegateToAsync(
                caller,
                TenantId,
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
    public async Task HandleDelegateTo_HappyPath_EmitsRouted()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var response = CreateResponse(target, caller);
        var message = CreateMessage();
        var threadId = Guid.NewGuid();
        Message? delivered = null;
        var agent = Substitute.For<IAgent>();

        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Do<Message>(m => delivered = m), Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await handlers.HandleDelegateToAsync(
            caller,
            TenantId,
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
        decision.TenantId.ShouldBe(TenantId);
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
    public async Task HandleDelegateTo_GitHubIssuePayload_EmitsIssueNumberMetadata()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var agent = Substitute.For<IAgent>();

        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(CreateResponse(target, caller));

        await handlers.HandleDelegateToAsync(
            caller,
            TenantId,
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
    public async Task HandleDelegateTo_NonGitHubIssuePayload_DoesNotEmitIssueMetadata()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var agent = Substitute.For<IAgent>();

        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(CreateResponse(target, caller));

        await handlers.HandleDelegateToAsync(
            caller,
            TenantId,
            target,
            CreateNonGitHubIssueMessage(number: 42),
            null,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        var decision = ReadSingleDecision(caller);
        decision.Metadata.ShouldBeNull();
    }

    [Fact]
    public async Task HandleDelegateTo_Exception_EmitsFailed()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var message = CreateMessage();
        var threadId = Guid.NewGuid();
        const string reason = "target owns the work";

        _agentProxyResolver.Resolve(target.Scheme, target.Id.ToString("N"))
            .Throws(new InvalidOperationException("resolver failed"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            handlers.HandleDelegateToAsync(
                caller,
                TenantId,
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
        decision.TenantId.ShouldBe(TenantId);
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
    public async Task HandleDelegateTo_HappyPath_WithReason_RoundTrips()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        const string reason = "specialized target owns this work";
        var agent = Substitute.For<IAgent>();

        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(CreateResponse(target, caller));

        await handlers.HandleDelegateToAsync(
            caller,
            TenantId,
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
    public async Task HandleFanoutTo_SelfTarget_ThrowsSelfDelegation()
    {
        // The self-delegation gate applies to fanout too — a caller cannot
        // include itself in the target list.
        var handlers = CreateHandlers();
        var caller = Unit();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleFanoutToAsync(
                caller,
                TenantId,
                [caller],
                CreateMessage(),
                null,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationSelfDelegation);
    }

    [Fact]
    public async Task HandleFanoutTo_TargetTenantMismatch_ThrowsCrossTenant()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var t1 = Agent(ChildAgentId);
        var t2 = Agent(OtherChildAgentId);

        _tenantResolver.GetTenantForAddressAsync(caller, Arg.Any<CancellationToken>()).Returns(TenantId);
        _tenantResolver.GetTenantForAddressAsync(t1, Arg.Any<CancellationToken>()).Returns(TenantId);
        _tenantResolver.GetTenantForAddressAsync(t2, Arg.Any<CancellationToken>()).Returns(OtherTenantId);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleFanoutToAsync(
                caller,
                TenantId,
                [t1, t2],
                CreateMessage(),
                null,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationCrossTenant);
    }

    [Fact]
    public async Task HandleFanoutTo_HappyPath_EmitsRouted()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var targetOne = Agent(ChildAgentId);
        var targetTwo = Agent(OtherChildAgentId);
        var responseOne = CreateResponse(targetOne, caller);
        var responseTwo = CreateResponse(targetTwo, caller);
        var message = CreateMessage();
        var threadId = Guid.NewGuid();
        const string reason = "ask both targets";
        var agentOne = Substitute.For<IAgent>();
        var agentTwo = Substitute.For<IAgent>();

        RegisterAgent(targetOne, agentOne);
        RegisterAgent(targetTwo, agentTwo);
        agentOne.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(responseOne);
        agentTwo.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(responseTwo);

        var results = await handlers.HandleFanoutToAsync(
            caller,
            TenantId,
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
        decision.TenantId.ShouldBe(TenantId);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Fanout);
        decision.Targets.ShouldBe([targetOne, targetTwo]);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Routed);
        decision.ResultMessageIds.ShouldBe([responseOne.Id, responseTwo.Id]);
        decision.Reason.ShouldBe(reason);
    }

    [Fact]
    public async Task HandleFanoutTo_PartialFailure_EmitsFailed()
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

        RegisterAgent(targetOne, successfulAgent);
        RegisterAgent(targetTwo, failingAgent);
        successfulAgent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(response);
        failingAgent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("target failed"));

        var results = await handlers.HandleFanoutToAsync(
            caller,
            TenantId,
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
        decision.TenantId.ShouldBe(TenantId);
        decision.ThreadId.ShouldBe(threadId);
        decision.InputMessageId.ShouldBe(message.Id);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Fanout);
        decision.Targets.ShouldBe([targetOne, targetTwo]);
        decision.Status.ShouldBe(OrchestrationDecisionStatus.Failed);
        decision.ResultMessageIds.ShouldBe([response.Id]);
    }

    private OrchestrationToolHandlers CreateHandlers(OrchestrationDepthCounter? depthCounter = null) =>
        new(
            _agentProxyResolver,
            depthCounter ?? new OrchestrationDepthCounter(),
            _logger,
            _activityEventBus,
            _tenantResolver);

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
