// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
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
    private static readonly Guid TenantId = OssTenantIds.Default;
    private static readonly Guid OtherTenantId = new("ffffffff-0000-0000-0000-000000000099");
    private static readonly Guid ChildAgentId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherChildAgentId = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid ChildSubUnitId = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid NonChildAgentId = new("aaaaaaaa-0000-0000-0000-000000000099");

    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IAgentProxyResolver _agentProxyResolver = Substitute.For<IAgentProxyResolver>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IOrchestrationTenantResolver _tenantResolver = Substitute.For<IOrchestrationTenantResolver>();
    private readonly ILogger<OrchestrationToolHandlers> _logger =
        Substitute.For<ILogger<OrchestrationToolHandlers>>();

    private readonly Dictionary<string, Address[]> _members = new();
    private readonly Dictionary<string, OrchestrationChildDescriptor[]> _descriptors = new();
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
                var descriptors = _descriptors.TryGetValue(actorId, out var d)
                    ? d
                    : members.Select(member => new OrchestrationChildDescriptor(
                        member,
                        DisplayName: string.Empty,
                        Kind: string.Equals(member.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase) ? "unit" : "agent",
                        ExecutionConfig: null)).ToArray();
                actor.GetMembersAsync(Arg.Any<CancellationToken>()).Returns(members);
                actor.GetChildDescriptorsAsync(Arg.Any<CancellationToken>()).Returns(descriptors);
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

        // Default resolver: every address belongs to the OSS default tenant.
        _tenantResolver.GetTenantForAddressAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(TenantId);
    }

    [Fact]
    public async Task HandleListChildren_NonUnitCaller_ThrowsCallerIsNotUnit()
    {
        var handlers = CreateHandlers();
        var caller = Agent(NonChildAgentId);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleListChildrenAsync(caller, TenantId, Guid.NewGuid(), TestContext.Current.CancellationToken));

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
            TenantId,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        result.Length.ShouldBe(1);
        result[0].Address.ShouldBe(child);
        await AssertNoDecisionPublishedAsync();
    }

    [Fact]
    public async Task HandleListChildren_ReturnsRichDescriptors_WithKindAndExecutionConfig()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var agentChild = Agent(ChildAgentId);
        var unitChild = new Address(Address.UnitScheme, ChildSubUnitId);
        var execConfig = JsonSerializer.SerializeToElement(new { Image = "ghcr.io/example/img:1", Agent = "claude" });

        RegisterMembers(caller, agentChild, unitChild);
        RegisterDescriptors(caller,
            new OrchestrationChildDescriptor(agentChild, "Backend Engineer", "agent", execConfig),
            new OrchestrationChildDescriptor(unitChild, "Engineering Sub-Team", "unit", null));

        var result = await handlers.HandleListChildrenAsync(
            caller,
            TenantId,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        result.Length.ShouldBe(2);

        result[0].Address.ShouldBe(agentChild);
        result[0].DisplayName.ShouldBe("Backend Engineer");
        result[0].Kind.ShouldBe("agent");
        result[0].ExecutionConfig.ShouldNotBeNull();
        result[0].ExecutionConfig!.Value.GetProperty("Image").GetString().ShouldBe("ghcr.io/example/img:1");

        result[1].Address.ShouldBe(unitChild);
        result[1].DisplayName.ShouldBe("Engineering Sub-Team");
        result[1].Kind.ShouldBe("unit");
        result[1].ExecutionConfig.ShouldBeNull();
    }

    [Fact]
    public async Task HandleListChildren_CallerTenantMismatch_ThrowsCrossTenant()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        RegisterMembers(caller, Agent(ChildAgentId));
        // Caller belongs to OtherTenantId, token claims TenantId.
        _tenantResolver.GetTenantForAddressAsync(caller, Arg.Any<CancellationToken>())
            .Returns(OtherTenantId);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleListChildrenAsync(caller, TenantId, Guid.NewGuid(), TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationCrossTenant);
    }

    [Fact]
    public async Task HandleDelegateToChild_NonUnitCaller_ThrowsCallerIsNotUnit()
    {
        var handlers = CreateHandlers();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleDelegateToChildAsync(
                Agent(NonChildAgentId),
                TenantId,
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
                TenantId,
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
                TenantId,
                Unit(),
                CreateMessage(),
                null,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationSelfDelegation);
    }

    [Fact]
    public async Task HandleDelegateToChild_TargetTenantMismatch_ThrowsCrossTenant()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        RegisterMembers(caller, target);

        // Caller resolves to TenantId; target resolves to OtherTenantId.
        _tenantResolver.GetTenantForAddressAsync(caller, Arg.Any<CancellationToken>())
            .Returns(TenantId);
        _tenantResolver.GetTenantForAddressAsync(target, Arg.Any<CancellationToken>())
            .Returns(OtherTenantId);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleDelegateToChildAsync(
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
            TenantId,
            target,
            CreateMessage(),
            null,
            threadId,
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleDelegateToChildAsync(
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
    public async Task HandleFanoutToChildren_NonUnitCaller_ThrowsCallerIsNotUnit()
    {
        var handlers = CreateHandlers();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleFanoutToChildrenAsync(
                Agent(NonChildAgentId),
                TenantId,
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
                TenantId,
                [Agent(NonChildAgentId)],
                CreateMessage(),
                null,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationTargetNotChild);
    }

    [Fact]
    public async Task HandleFanoutToChildren_TargetTenantMismatch_ThrowsCrossTenant()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var t1 = Agent(ChildAgentId);
        var t2 = Agent(OtherChildAgentId);

        RegisterMembers(caller, t1, t2);
        _tenantResolver.GetTenantForAddressAsync(caller, Arg.Any<CancellationToken>()).Returns(TenantId);
        _tenantResolver.GetTenantForAddressAsync(t1, Arg.Any<CancellationToken>()).Returns(TenantId);
        _tenantResolver.GetTenantForAddressAsync(t2, Arg.Any<CancellationToken>()).Returns(OtherTenantId);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleFanoutToChildrenAsync(
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

    [Fact]
    public async Task HandleInspectChild_NonUnitCaller_ThrowsCallerIsNotUnit()
    {
        var handlers = CreateHandlers();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleInspectChildAsync(
                Agent(NonChildAgentId),
                TenantId,
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
                TenantId,
                Agent(NonChildAgentId),
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationTargetNotChild);
    }

    [Fact]
    public async Task HandleInspectChild_TargetTenantMismatch_ThrowsCrossTenant()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        RegisterMembers(caller, target);

        _tenantResolver.GetTenantForAddressAsync(caller, Arg.Any<CancellationToken>()).Returns(TenantId);
        _tenantResolver.GetTenantForAddressAsync(target, Arg.Any<CancellationToken>()).Returns(OtherTenantId);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleInspectChildAsync(
                caller,
                TenantId,
                target,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationCrossTenant);
    }

    [Fact]
    public async Task HandleInspectChild_ReturnsRichDescriptor()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        RegisterMembers(caller, target);
        RegisterDescriptors(caller,
            new OrchestrationChildDescriptor(target, "Backend Engineer", "agent", null));

        // Probe response: idle agent.
        var agent = Substitute.For<IAgent>();
        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Is<Message>(m => m.Type == MessageType.StatusQuery), Arg.Any<CancellationToken>())
            .Returns(CreateStatusResponse(target, status: "Idle", activeThreadId: null));

        var meta = await handlers.HandleInspectChildAsync(
            caller,
            TenantId,
            target,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        meta["address"].ShouldBe(target.ToString());
        meta["displayName"].ShouldBe("Backend Engineer");
        meta["kind"].ShouldBe("agent");
        meta["status"].ShouldBe("ready");
        await AssertNoDecisionPublishedAsync();
    }

    [Fact]
    public async Task HandleQueryChildStatus_NonUnitCaller_ThrowsCallerIsNotUnit()
    {
        var handlers = CreateHandlers();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleQueryChildStatusAsync(
                Agent(NonChildAgentId),
                TenantId,
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
                TenantId,
                Agent(NonChildAgentId),
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationTargetNotChild);
    }

    [Fact]
    public async Task HandleQueryChildStatus_TargetTenantMismatch_ThrowsCrossTenant()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        RegisterMembers(caller, target);

        _tenantResolver.GetTenantForAddressAsync(caller, Arg.Any<CancellationToken>()).Returns(TenantId);
        _tenantResolver.GetTenantForAddressAsync(target, Arg.Any<CancellationToken>()).Returns(OtherTenantId);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleQueryChildStatusAsync(
                caller,
                TenantId,
                target,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationCrossTenant);
    }

    [Fact]
    public async Task HandleQueryChildStatus_IdleAgent_ReturnsReady()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        RegisterMembers(caller, target);

        var agent = Substitute.For<IAgent>();
        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Is<Message>(m => m.Type == MessageType.StatusQuery), Arg.Any<CancellationToken>())
            .Returns(CreateStatusResponse(target, status: "Idle", activeThreadId: null));

        var status = await handlers.HandleQueryChildStatusAsync(
            caller,
            TenantId,
            target,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        status.Status.ShouldBe("ready");
        status.BusyOnThread.ShouldBeNull();
        await AssertNoDecisionPublishedAsync();
    }

    [Fact]
    public async Task HandleQueryChildStatus_ActiveAgent_ReturnsBusyWithThread()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var threadId = "thread-deadbeef";
        RegisterMembers(caller, target);

        var agent = Substitute.For<IAgent>();
        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Is<Message>(m => m.Type == MessageType.StatusQuery), Arg.Any<CancellationToken>())
            .Returns(CreateStatusResponse(target, status: "Active", activeThreadId: threadId));

        var status = await handlers.HandleQueryChildStatusAsync(
            caller,
            TenantId,
            target,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        status.Status.ShouldBe("busy");
        status.BusyOnThread.ShouldBe(threadId);
    }

    [Fact]
    public async Task HandleQueryChildStatus_ProbeFails_ReturnsUnknown()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        RegisterMembers(caller, target);

        var agent = Substitute.For<IAgent>();
        RegisterAgent(target, agent);
        agent.ReceiveAsync(Arg.Is<Message>(m => m.Type == MessageType.StatusQuery), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("probe failed"));

        var status = await handlers.HandleQueryChildStatusAsync(
            caller,
            TenantId,
            target,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        status.Status.ShouldBe("unknown");
        status.LastActivityAt.ShouldBeNull();
        status.BusyOnThread.ShouldBeNull();
    }

    [Fact]
    public async Task HandleQueryChildStatus_StoppedUnitChild_ReturnsStopped()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var subUnit = new Address(Address.UnitScheme, ChildSubUnitId);
        RegisterMembers(caller, subUnit);

        var unitAgent = Substitute.For<IAgent>();
        RegisterAgent(subUnit, unitAgent);
        unitAgent.ReceiveAsync(Arg.Is<Message>(m => m.Type == MessageType.StatusQuery), Arg.Any<CancellationToken>())
            .Returns(CreateStatusResponse(subUnit, status: nameof(UnitStatus.Stopped), activeThreadId: null));

        var status = await handlers.HandleQueryChildStatusAsync(
            caller,
            TenantId,
            subUnit,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        status.Status.ShouldBe("stopped");
    }

    private OrchestrationToolHandlers CreateHandlers(OrchestrationDepthCounter? depthCounter = null) =>
        new(
            _actorProxyFactory,
            _agentProxyResolver,
            depthCounter ?? new OrchestrationDepthCounter(),
            _logger,
            _activityEventBus,
            _tenantResolver);

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

    private void RegisterDescriptors(Address unit, params OrchestrationChildDescriptor[] descriptors) =>
        _descriptors[unit.Id.ToString("N")] = descriptors;

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

    private static Message CreateStatusResponse(Address from, string status, string? activeThreadId)
    {
        // #2076 / ADR-0030 §3 §44: AgentActor's StatusQuery payload now
        // exposes a per-thread ThreadDepths map instead of the binary
        // ActiveThreadId / PendingConversationCount shape. The
        // orchestration probe surfaces a representative thread id from
        // the depth map.
        var payload = activeThreadId is null
            ? JsonSerializer.SerializeToElement(new
            {
                Status = status,
                ThreadDepths = new Dictionary<string, int>(),
            })
            : JsonSerializer.SerializeToElement(new
            {
                Status = status,
                ThreadDepths = new Dictionary<string, int> { [activeThreadId] = 1 },
            });

        return new Message(
            Guid.NewGuid(),
            from,
            from,
            MessageType.StatusQuery,
            null,
            payload,
            DateTimeOffset.UtcNow);
    }
}
