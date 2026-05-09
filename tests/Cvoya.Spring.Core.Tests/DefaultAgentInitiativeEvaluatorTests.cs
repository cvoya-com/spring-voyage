// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Policies;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DefaultAgentInitiativeEvaluator"/> — the PR-PLAT-INIT-1
/// governance seam that answers "act now / act with confirmation / defer" for
/// Proactive and Autonomous agents. Exercises every state: Reactive defer,
/// Proactive confirmation + empty-signal defer, Autonomous happy path, budget
/// + action-deny fallbacks, fail-closed exception paths, and runtime level
/// changes.
/// </summary>
public class DefaultAgentInitiativeEvaluatorTests
{
    private static readonly IReadOnlyList<JsonElement> OneSignal = new[]
    {
        JsonDocument.Parse("""{ "summary": "a signal" }""").RootElement,
    };

    private static InitiativeEvaluationContext ContextFor(
        InitiativeAction? action = null,
        IReadOnlyList<JsonElement>? signals = null) =>
        new(
            AgentId: "ada",
            Action: action ?? new InitiativeAction("send-message", EstimatedCost: 0.01m),
            Signals: signals ?? OneSignal);

    [Fact]
    public async Task EvaluateAsync_Reactive_DefersWithoutConsultingGates()
    {
        var policyStore = FakeAgentPolicyStore.With("ada", InitiativeLevel.Attentive, new InitiativePolicy());
        var enforcer = ThrowingEnforcer.Instance;
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var result = await sut.EvaluateAsync(ContextFor(), TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(InitiativeEvaluationDecision.Defer);
        result.EffectiveLevel.ShouldBe(InitiativeLevel.Attentive);
        result.Reason!.ShouldContain("below Proactive");
        enforcer.InitiativeCalls.ShouldBe(0);
        enforcer.CostCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EvaluateAsync_ProactiveWithEmptySignals_Defers()
    {
        var policyStore = FakeAgentPolicyStore.With(
            "ada", InitiativeLevel.Proactive, new InitiativePolicy(MaxLevel: InitiativeLevel.Proactive));
        var enforcer = new RecordingEnforcer();
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var result = await sut.EvaluateAsync(
            ContextFor(signals: Array.Empty<JsonElement>()),
            TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(InitiativeEvaluationDecision.Defer);
        result.Reason!.ShouldContain("no signals");
        enforcer.InitiativeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EvaluateAsync_Proactive_AlwaysReturnsConfirmation()
    {
        var policyStore = FakeAgentPolicyStore.With(
            "ada", InitiativeLevel.Proactive, new InitiativePolicy(MaxLevel: InitiativeLevel.Proactive));
        var enforcer = new RecordingEnforcer();
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var result = await sut.EvaluateAsync(ContextFor(), TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(InitiativeEvaluationDecision.ActWithConfirmation);
        result.EffectiveLevel.ShouldBe(InitiativeLevel.Proactive);
        result.Reason!.ShouldContain("proactive");
        result.FailedClosed.ShouldBeFalse();
        enforcer.InitiativeCalls.ShouldBe(1);
        enforcer.CostCalls.ShouldBe(1);
    }

    [Fact]
    public async Task EvaluateAsync_ProactiveRateLimitedByBudgetTracker_DowngradesToDefer()
    {
        // A proactive agent whose budget is exhausted — the cost enforcer returns
        // deny. The evaluator must surface a confirmation-required decision with
        // a reason so the caller sees the rate-limit event rather than a silent
        // act. (This IS the rate-limit story: the Tier2Config MaxCallsPerHour /
        // MaxCostPerDay caps are enforced via the cost gate.)
        var policyStore = FakeAgentPolicyStore.With(
            "ada", InitiativeLevel.Proactive, new InitiativePolicy(MaxLevel: InitiativeLevel.Proactive));
        var enforcer = new RecordingEnforcer
        {
            CostDecision = PolicyDecision.Deny("daily cap breached", "engineering"),
        };
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var result = await sut.EvaluateAsync(
            ContextFor(new InitiativeAction("send-message", EstimatedCost: 2.00m)),
            TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(InitiativeEvaluationDecision.ActWithConfirmation);
        result.Reason!.ShouldContain("daily cap breached");
        result.FailedClosed.ShouldBeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_Autonomous_ReversibleAndInBudget_ActsAutonomously()
    {
        var policyStore = FakeAgentPolicyStore.With(
            "ada", InitiativeLevel.Autonomous, new InitiativePolicy(MaxLevel: InitiativeLevel.Autonomous));
        var enforcer = new RecordingEnforcer();
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var result = await sut.EvaluateAsync(
            ContextFor(new InitiativeAction("send-message", EstimatedCost: 0.01m, IsReversible: true)),
            TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(InitiativeEvaluationDecision.ActAutonomously);
        result.EffectiveLevel.ShouldBe(InitiativeLevel.Autonomous);
        result.FailedClosed.ShouldBeFalse();
        enforcer.InitiativeCalls.ShouldBe(1);
        enforcer.CostCalls.ShouldBe(1);
    }

    [Fact]
    public async Task EvaluateAsync_Autonomous_IrreversibleAction_DowngradesToConfirmation()
    {
        var policyStore = FakeAgentPolicyStore.With(
            "ada", InitiativeLevel.Autonomous, new InitiativePolicy(MaxLevel: InitiativeLevel.Autonomous));
        var enforcer = new RecordingEnforcer();
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var result = await sut.EvaluateAsync(
            ContextFor(new InitiativeAction("delete-repo", EstimatedCost: 0m, IsReversible: false)),
            TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(InitiativeEvaluationDecision.ActWithConfirmation);
        result.Reason!.ShouldContain("not marked as reversible");
        result.FailedClosed.ShouldBeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_Autonomous_RequireUnitApproval_DowngradesToConfirmation()
    {
        var policyStore = FakeAgentPolicyStore.With(
            "ada",
            InitiativeLevel.Autonomous,
            new InitiativePolicy(MaxLevel: InitiativeLevel.Autonomous, RequireUnitApproval: true));
        var enforcer = new RecordingEnforcer();
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var result = await sut.EvaluateAsync(ContextFor(), TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(InitiativeEvaluationDecision.ActWithConfirmation);
        result.Reason!.ShouldContain("unit policy requires approval");
        result.FailedClosed.ShouldBeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_Autonomous_ActionBlockedByUnitPolicy_DowngradesToConfirmation()
    {
        var policyStore = FakeAgentPolicyStore.With(
            "ada", InitiativeLevel.Autonomous, new InitiativePolicy(MaxLevel: InitiativeLevel.Autonomous));
        var enforcer = new RecordingEnforcer
        {
            ActionDecision = PolicyDecision.Deny("action blocked by unit 'engineering' initiative policy", "engineering"),
        };
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var result = await sut.EvaluateAsync(
            ContextFor(new InitiativeAction("delete-repo")),
            TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(InitiativeEvaluationDecision.ActWithConfirmation);
        result.Reason!.ShouldContain("blocked by unit");
        result.FailedClosed.ShouldBeFalse();
        // Cost gate is not consulted once the action has been denied — no
        // point in checking budget on an action that is already rejected.
        enforcer.CostCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EvaluateAsync_Autonomous_CostGateDenies_DowngradesToConfirmation()
    {
        var policyStore = FakeAgentPolicyStore.With(
            "ada", InitiativeLevel.Autonomous, new InitiativePolicy(MaxLevel: InitiativeLevel.Autonomous));
        var enforcer = new RecordingEnforcer
        {
            CostDecision = PolicyDecision.Deny("per-invocation cap exceeded", "engineering"),
        };
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var result = await sut.EvaluateAsync(
            ContextFor(new InitiativeAction("send-message", EstimatedCost: 100m)),
            TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(InitiativeEvaluationDecision.ActWithConfirmation);
        result.Reason!.ShouldContain("per-invocation cap exceeded");
        result.FailedClosed.ShouldBeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_Autonomous_CostGateThrows_FailsClosedToConfirmation()
    {
        // When the budget-check layer cannot resolve (the cost-query service
        // is down, the repo throws), the evaluator MUST downgrade to
        // confirmation-required with FailedClosed = true. Never silently
        // authorise an action whose cost cannot be checked.
        var policyStore = FakeAgentPolicyStore.With(
            "ada", InitiativeLevel.Autonomous, new InitiativePolicy(MaxLevel: InitiativeLevel.Autonomous));
        var enforcer = new RecordingEnforcer
        {
            CostException = new InvalidOperationException("cost service down"),
        };
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var result = await sut.EvaluateAsync(
            ContextFor(new InitiativeAction("send-message", EstimatedCost: 1m)),
            TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(InitiativeEvaluationDecision.ActWithConfirmation);
        result.FailedClosed.ShouldBeTrue();
        result.Reason!.ShouldContain("cost gate unresolved");
        result.Reason!.ShouldContain("cost service down");
    }

    [Fact]
    public async Task EvaluateAsync_Autonomous_ActionGateThrows_FailsClosedToConfirmation()
    {
        var policyStore = FakeAgentPolicyStore.With(
            "ada", InitiativeLevel.Autonomous, new InitiativePolicy(MaxLevel: InitiativeLevel.Autonomous));
        var enforcer = new RecordingEnforcer
        {
            ActionException = new InvalidOperationException("unit policy repo offline"),
        };
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var result = await sut.EvaluateAsync(ContextFor(), TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(InitiativeEvaluationDecision.ActWithConfirmation);
        result.FailedClosed.ShouldBeTrue();
        result.Reason!.ShouldContain("initiative action gate unresolved");
    }

    [Fact]
    public async Task EvaluateAsync_PolicyLookupThrows_DefersFailClosed()
    {
        // When the policy store itself is broken (we cannot even determine the
        // agent's level), we have no basis to act — fall all the way back to
        // Defer. The cost / action gates are never even reached.
        var policyStore = FakeAgentPolicyStore.WithLevelException(
            "ada", new InvalidOperationException("state store offline"));
        var enforcer = new RecordingEnforcer();
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var result = await sut.EvaluateAsync(ContextFor(), TestContext.Current.CancellationToken);

        result.Decision.ShouldBe(InitiativeEvaluationDecision.Defer);
        result.Reason!.ShouldContain("policy lookup failed");
        enforcer.InitiativeCalls.ShouldBe(0);
        enforcer.CostCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EvaluateAsync_LevelChangeAtRuntime_PropagatesOnNextCall()
    {
        // Policy is re-read on every call — no snapshot. Flip the level from
        // Autonomous to Proactive between two calls and the second call must
        // downgrade to ActWithConfirmation without reinitialising the evaluator.
        var policyStore = FakeAgentPolicyStore.With(
            "ada", InitiativeLevel.Autonomous, new InitiativePolicy(MaxLevel: InitiativeLevel.Autonomous));
        var enforcer = new RecordingEnforcer();
        var sut = new DefaultAgentInitiativeEvaluator(policyStore, enforcer);

        var first = await sut.EvaluateAsync(ContextFor(), TestContext.Current.CancellationToken);
        first.Decision.ShouldBe(InitiativeEvaluationDecision.ActAutonomously);

        // Runtime change: operator bumps MaxLevel down.
        policyStore.SetLevel("ada", InitiativeLevel.Proactive);
        policyStore.SetPolicy("ada", new InitiativePolicy(MaxLevel: InitiativeLevel.Proactive));

        var second = await sut.EvaluateAsync(ContextFor(), TestContext.Current.CancellationToken);
        second.Decision.ShouldBe(InitiativeEvaluationDecision.ActWithConfirmation);
        second.EffectiveLevel.ShouldBe(InitiativeLevel.Proactive);
    }

    [Fact]
    public async Task EvaluateAsync_NullContext_Throws()
    {
        var sut = new DefaultAgentInitiativeEvaluator(
            FakeAgentPolicyStore.With("ada", InitiativeLevel.Passive, new InitiativePolicy()),
            new RecordingEnforcer());

        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await sut.EvaluateAsync(null!, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Hand-rolled <see cref="IAgentPolicyStore"/> fake so the Core test project
    /// stays free of mocking-library churn. Values are mutable so tests can
    /// exercise the runtime-level-change path.
    /// </summary>
    private sealed class FakeAgentPolicyStore : IAgentPolicyStore
    {
        private readonly Dictionary<string, InitiativeLevel> _levels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, InitiativePolicy> _policies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _levelExceptions = new(StringComparer.Ordinal);

        public static FakeAgentPolicyStore With(string agentId, InitiativeLevel level, InitiativePolicy policy)
        {
            var store = new FakeAgentPolicyStore();
            store.SetLevel(agentId, level);
            store.SetPolicy(agentId, policy);
            return store;
        }

        public static FakeAgentPolicyStore WithLevelException(string agentId, Exception exception)
        {
            var store = new FakeAgentPolicyStore();
            store._levelExceptions[agentId] = exception;
            return store;
        }

        public void SetLevel(string agentId, InitiativeLevel level) => _levels[agentId] = level;
        public void SetPolicy(string agentId, InitiativePolicy policy) => _policies[agentId] = policy;

        public Task<InitiativePolicy> GetPolicyAsync(string targetId, CancellationToken cancellationToken) =>
            Task.FromResult(_policies.TryGetValue(targetId, out var p) ? p : new InitiativePolicy());

        public Task SetPolicyAsync(string targetId, InitiativePolicy policy, CancellationToken cancellationToken)
        {
            _policies[targetId] = policy;
            return Task.CompletedTask;
        }

        public Task<InitiativeLevel> GetEffectiveLevelAsync(string agentId, CancellationToken cancellationToken)
        {
            if (_levelExceptions.TryGetValue(agentId, out var ex))
            {
                throw ex;
            }
            return Task.FromResult(_levels.TryGetValue(agentId, out var l) ? l : InitiativeLevel.Passive);
        }

        public Task SetAgentUnitAsync(string agentId, string? unitId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string?> GetAgentUnitAsync(string agentId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>
    /// <see cref="IUnitPolicyEnforcer"/> fake that records call counts and lets
    /// each test set per-gate decisions or throws. Every method defaults to
    /// <see cref="PolicyDecision.Allowed"/> so tests stay narrow.
    /// </summary>
    private sealed class RecordingEnforcer : IUnitPolicyEnforcer
    {
        public PolicyDecision ActionDecision { get; set; } = PolicyDecision.Allowed;
        public PolicyDecision CostDecision { get; set; } = PolicyDecision.Allowed;
        public Exception? ActionException { get; set; }
        public Exception? CostException { get; set; }

        public int InitiativeCalls { get; private set; }
        public int CostCalls { get; private set; }

        public Task<PolicyDecision> EvaluateInitiativeActionAsync(
            string agentId,
            string actionType,
            CancellationToken cancellationToken = default)
        {
            InitiativeCalls++;
            if (ActionException is not null)
            {
                throw ActionException;
            }
            return Task.FromResult(ActionDecision);
        }

        public Task<PolicyDecision> EvaluateCostAsync(
            string agentId,
            decimal projectedCost,
            CancellationToken cancellationToken = default)
        {
            CostCalls++;
            if (CostException is not null)
            {
                throw CostException;
            }
            return Task.FromResult(CostDecision);
        }

        public Task<PolicyDecision> EvaluateSkillInvocationAsync(string agentId, string toolName, CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyDecision.Allowed);

        public Task<PolicyDecision> EvaluateModelAsync(string agentId, string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyDecision.Allowed);

        public Task<PolicyDecision> EvaluateExecutionModeAsync(string agentId, Cvoya.Spring.Core.Agents.AgentExecutionMode mode, CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyDecision.Allowed);

        public Task<ExecutionModeResolution> ResolveExecutionModeAsync(string agentId, Cvoya.Spring.Core.Agents.AgentExecutionMode mode, CancellationToken cancellationToken = default) =>
            Task.FromResult(ExecutionModeResolution.AllowAsIs(mode));
    }

    /// <summary>
    /// Enforcer that throws on every gate — used to prove the Reactive path
    /// never consults the enforcer at all.
    /// </summary>
    private sealed class ThrowingEnforcer : IUnitPolicyEnforcer
    {
        public static readonly ThrowingEnforcer Instance = new();

        public int InitiativeCalls => 0;
        public int CostCalls => 0;

        public Task<PolicyDecision> EvaluateSkillInvocationAsync(string agentId, string toolName, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("skill gate should never be consulted");

        public Task<PolicyDecision> EvaluateModelAsync(string agentId, string modelId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("model gate should never be consulted");

        public Task<PolicyDecision> EvaluateCostAsync(string agentId, decimal projectedCost, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("cost gate should never be consulted");

        public Task<PolicyDecision> EvaluateExecutionModeAsync(string agentId, Cvoya.Spring.Core.Agents.AgentExecutionMode mode, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("execution-mode gate should never be consulted");

        public Task<ExecutionModeResolution> ResolveExecutionModeAsync(string agentId, Cvoya.Spring.Core.Agents.AgentExecutionMode mode, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("execution-mode gate should never be consulted");

        public Task<PolicyDecision> EvaluateInitiativeActionAsync(string agentId, string actionType, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("initiative gate should never be consulted");
    }
}
