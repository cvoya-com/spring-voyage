// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Units;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DefaultUnitPolicyEnforcer"/> skill-policy evaluation.
/// Covers: allowed (no membership, empty policy, whitelist hit, blacklist
/// miss), denied (whitelist miss, blacklist hit, blacklist takes precedence),
/// multi-unit composition (first denying unit wins), case-insensitive
/// matching, and degenerate inputs.
/// </summary>
public class DefaultUnitPolicyEnforcerTests
{
    // Stable UUIDs used as actor IDs in every test. The enforcer requires
    // UUID-shaped agentId strings (post #1492 migration) and uses
    // UnitId.ToString() as the policy-repo key.
    private static readonly Guid AgentAda = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UnitEngineering = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid UnitMarketing = new("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public async Task EvaluateSkillInvocation_NoMemberships_Allowed()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            new FakeMembershipRepository(),
            new FakePolicyRepository());

        var result = await enforcer.EvaluateSkillInvocationAsync(AgentAda.ToString(), "search", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateSkillInvocation_UnitWithNoPolicy_Allowed()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            new FakePolicyRepository());

        var result = await enforcer.EvaluateSkillInvocationAsync(AgentAda.ToString(), "search", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateSkillInvocation_UnitWithEmptyPolicy_Allowed()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, UnitPolicy.Empty)));

        var result = await enforcer.EvaluateSkillInvocationAsync(AgentAda.ToString(), "search", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateSkillInvocation_ToolInBlockList_Denied()
    {
        var policy = new UnitPolicy(new SkillPolicy(Blocked: new[] { "delete_repo" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateSkillInvocationAsync(AgentAda.ToString(), "delete_repo", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.DenyingUnitId.ShouldBe(UnitEngineering.ToString());
        result.Reason!.ShouldContain("blocked");
    }

    [Fact]
    public async Task EvaluateSkillInvocation_ToolNotInWhitelist_Denied()
    {
        var policy = new UnitPolicy(new SkillPolicy(Allowed: new[] { "search" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateSkillInvocationAsync(AgentAda.ToString(), "delete_repo", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.DenyingUnitId.ShouldBe(UnitEngineering.ToString());
    }

    [Fact]
    public async Task EvaluateSkillInvocation_ToolInWhitelist_Allowed()
    {
        var policy = new UnitPolicy(new SkillPolicy(Allowed: new[] { "search", "summarize" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateSkillInvocationAsync(AgentAda.ToString(), "summarize", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateSkillInvocation_BlockPrecedesAllow_Denied()
    {
        var policy = new UnitPolicy(new SkillPolicy(
            Allowed: new[] { "search", "delete_repo" },
            Blocked: new[] { "delete_repo" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateSkillInvocationAsync(AgentAda.ToString(), "delete_repo", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.Reason!.ShouldContain("blocked");
    }

    [Fact]
    public async Task EvaluateSkillInvocation_CaseInsensitiveMatch()
    {
        var policy = new UnitPolicy(new SkillPolicy(Blocked: new[] { "Delete_Repo" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateSkillInvocationAsync(AgentAda.ToString(), "DELETE_REPO", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task EvaluateSkillInvocation_EmptyAllowedList_DeniesEverything()
    {
        // Allowed: [] is a legitimate "disable every tool" state, distinct
        // from Allowed: null which means "no whitelist constraint".
        var policy = new UnitPolicy(new SkillPolicy(Allowed: Array.Empty<string>()));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateSkillInvocationAsync(AgentAda.ToString(), "search", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task EvaluateSkillInvocation_MultipleUnits_FirstDenyingUnitWins()
    {
        // Agent belongs to two units — marketing is permissive, engineering
        // blocks the tool. Either iteration order is legal; the test asserts
        // that SOME denying unit is identified.
        var membershipRepo = FakeMembershipRepository.With(
            (UnitMarketing, AgentAda),
            (UnitEngineering, AgentAda));
        var policyRepo = FakePolicyRepository.With(
            (UnitMarketing, UnitPolicy.Empty),
            (UnitEngineering, new UnitPolicy(new SkillPolicy(Blocked: new[] { "delete_repo" }))));

        var enforcer = new DefaultUnitPolicyEnforcer(membershipRepo, policyRepo);

        var result = await enforcer.EvaluateSkillInvocationAsync(AgentAda.ToString(), "delete_repo", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.DenyingUnitId.ShouldBe(UnitEngineering.ToString());
    }

    [Fact]
    public async Task EvaluateSkillInvocation_EmptyAgentOrTool_Allowed()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            new FakeMembershipRepository(),
            new FakePolicyRepository());

        var ct = TestContext.Current.CancellationToken;
        // Empty string is not a valid UUID — enforcer returns Allowed immediately.
        (await enforcer.EvaluateSkillInvocationAsync("", "search", ct))
            .IsAllowed.ShouldBeTrue();
        (await enforcer.EvaluateSkillInvocationAsync(AgentAda.ToString(), "", ct))
            .IsAllowed.ShouldBeTrue();
    }

    // -----------------------------------------------------------------
    // Model caps (#247)
    // -----------------------------------------------------------------

    [Fact]
    public async Task EvaluateModel_NoPolicy_Allowed()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, UnitPolicy.Empty)));

        var result = await enforcer.EvaluateModelAsync(AgentAda.ToString(), "gpt-4", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateModel_ModelInBlocklist_Denied()
    {
        var policy = new UnitPolicy(Model: new ModelPolicy(Blocked: new[] { "gpt-4" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateModelAsync(AgentAda.ToString(), "gpt-4", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.DenyingUnitId.ShouldBe(UnitEngineering.ToString());
        result.Reason!.ShouldContain("blocked");
    }

    [Fact]
    public async Task EvaluateModel_ModelNotInWhitelist_Denied()
    {
        var policy = new UnitPolicy(Model: new ModelPolicy(Allowed: new[] { "claude-sonnet" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateModelAsync(AgentAda.ToString(), "gpt-4", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.Reason!.ShouldContain("not in unit");
    }

    [Fact]
    public async Task EvaluateModel_ModelInWhitelist_Allowed()
    {
        var policy = new UnitPolicy(Model: new ModelPolicy(Allowed: new[] { "claude-sonnet", "gpt-4" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateModelAsync(AgentAda.ToString(), "Claude-Sonnet", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateModel_BlocklistWinsOverAllowlist()
    {
        var policy = new UnitPolicy(Model: new ModelPolicy(
            Allowed: new[] { "gpt-4" },
            Blocked: new[] { "gpt-4" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateModelAsync(AgentAda.ToString(), "gpt-4", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.Reason!.ShouldContain("blocked");
    }

    // -----------------------------------------------------------------
    // Cost caps (#248)
    // -----------------------------------------------------------------

    [Fact]
    public async Task EvaluateCost_NoPolicy_Allowed()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, UnitPolicy.Empty)),
            new FakeCostQueryService());

        var result = await enforcer.EvaluateCostAsync(AgentAda.ToString(), 1.00m, TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateCost_PerInvocationCap_ExceededDenied()
    {
        var policy = new UnitPolicy(Cost: new CostPolicy(MaxCostPerInvocation: 0.50m));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateCostAsync(AgentAda.ToString(), 0.75m, TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.DenyingUnitId.ShouldBe(UnitEngineering.ToString());
        result.Reason!.ShouldContain("per-invocation");
    }

    [Fact]
    public async Task EvaluateCost_PerInvocationCap_AtOrBelowAllowed()
    {
        var policy = new UnitPolicy(Cost: new CostPolicy(MaxCostPerInvocation: 0.50m));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateCostAsync(AgentAda.ToString(), 0.50m, TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateCost_HourlyCap_ExceededByWindowSum_Denied()
    {
        var policy = new UnitPolicy(Cost: new CostPolicy(MaxCostPerHour: 2.00m));
        var costs = new FakeCostQueryService();
        costs.SetHourlyCost(AgentAda.ToString(), 1.80m);

        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)),
            costs);

        var result = await enforcer.EvaluateCostAsync(AgentAda.ToString(), 0.50m, TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.Reason!.ShouldContain("per-hour");
    }

    [Fact]
    public async Task EvaluateCost_HourlyCap_WithinWindowAllowed()
    {
        var policy = new UnitPolicy(Cost: new CostPolicy(MaxCostPerHour: 2.00m));
        var costs = new FakeCostQueryService();
        costs.SetHourlyCost(AgentAda.ToString(), 1.00m);

        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)),
            costs);

        var result = await enforcer.EvaluateCostAsync(AgentAda.ToString(), 0.50m, TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateCost_DailyCap_ExceededByWindowSum_Denied()
    {
        var policy = new UnitPolicy(Cost: new CostPolicy(MaxCostPerDay: 10.00m));
        var costs = new FakeCostQueryService();
        costs.SetDailyCost(AgentAda.ToString(), 9.80m);

        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)),
            costs);

        var result = await enforcer.EvaluateCostAsync(AgentAda.ToString(), 0.50m, TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.Reason!.ShouldContain("per-day");
    }

    // -----------------------------------------------------------------
    // Execution mode (#249)
    // -----------------------------------------------------------------

    [Fact]
    public async Task ResolveExecutionMode_NoPolicy_ReturnsInputUnchanged()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, UnitPolicy.Empty)));

        var resolution = await enforcer.ResolveExecutionModeAsync(
            AgentAda.ToString(), AgentExecutionMode.Auto, TestContext.Current.CancellationToken);

        resolution.Decision.IsAllowed.ShouldBeTrue();
        resolution.Mode.ShouldBe(AgentExecutionMode.Auto);
    }

    [Fact]
    public async Task ResolveExecutionMode_Forced_CoercesRegardlessOfRequestedMode()
    {
        var policy = new UnitPolicy(ExecutionMode: new ExecutionModePolicy(Forced: AgentExecutionMode.OnDemand));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var resolution = await enforcer.ResolveExecutionModeAsync(
            AgentAda.ToString(), AgentExecutionMode.Auto, TestContext.Current.CancellationToken);

        resolution.Decision.IsAllowed.ShouldBeTrue();
        resolution.Mode.ShouldBe(AgentExecutionMode.OnDemand);
    }

    [Fact]
    public async Task ResolveExecutionMode_AllowlistMiss_Denies()
    {
        var policy = new UnitPolicy(ExecutionMode: new ExecutionModePolicy(
            Allowed: new[] { AgentExecutionMode.OnDemand }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var resolution = await enforcer.ResolveExecutionModeAsync(
            AgentAda.ToString(), AgentExecutionMode.Auto, TestContext.Current.CancellationToken);

        resolution.Decision.IsAllowed.ShouldBeFalse();
        resolution.Decision.DenyingUnitId.ShouldBe(UnitEngineering.ToString());
    }

    [Fact]
    public async Task ResolveExecutionMode_AllowlistHit_Allowed()
    {
        var policy = new UnitPolicy(ExecutionMode: new ExecutionModePolicy(
            Allowed: new[] { AgentExecutionMode.OnDemand, AgentExecutionMode.Auto }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var resolution = await enforcer.ResolveExecutionModeAsync(
            AgentAda.ToString(), AgentExecutionMode.OnDemand, TestContext.Current.CancellationToken);

        resolution.Decision.IsAllowed.ShouldBeTrue();
        resolution.Mode.ShouldBe(AgentExecutionMode.OnDemand);
    }

    [Fact]
    public async Task EvaluateExecutionMode_ForcedCoercion_IsDenied()
    {
        // EvaluateExecutionModeAsync is the "strict" version — a coercion
        // counts as a deny because the caller will NOT dispatch under the
        // input mode. ResolveExecutionModeAsync remains Allow-with-coerced-mode.
        var policy = new UnitPolicy(ExecutionMode: new ExecutionModePolicy(Forced: AgentExecutionMode.OnDemand));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateExecutionModeAsync(
            AgentAda.ToString(), AgentExecutionMode.Auto, TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.Reason!.ShouldContain("coerced");
    }

    // -----------------------------------------------------------------
    // Initiative (#250)
    // -----------------------------------------------------------------

    [Fact]
    public async Task EvaluateInitiativeAction_NoPolicy_Allowed()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, UnitPolicy.Empty)));

        var result = await enforcer.EvaluateInitiativeActionAsync(
            AgentAda.ToString(), "send-message", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateInitiativeAction_Blocked_Denied()
    {
        var policy = new UnitPolicy(Initiative: new InitiativePolicy(
            BlockedActions: new[] { "send-message" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateInitiativeActionAsync(
            AgentAda.ToString(), "send-message", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.Reason!.ShouldContain("blocked");
    }

    [Fact]
    public async Task EvaluateInitiativeAction_NotInWhitelist_Denied()
    {
        var policy = new UnitPolicy(Initiative: new InitiativePolicy(
            AllowedActions: new[] { "start-conversation" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateInitiativeActionAsync(
            AgentAda.ToString(), "send-message", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task EvaluateInitiativeAction_InWhitelist_Allowed()
    {
        var policy = new UnitPolicy(Initiative: new InitiativePolicy(
            AllowedActions: new[] { "send-message", "start-conversation" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With((UnitEngineering, AgentAda)),
            FakePolicyRepository.With((UnitEngineering, policy)));

        var result = await enforcer.EvaluateInitiativeActionAsync(
            AgentAda.ToString(), "send-message", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    /// <summary>
    /// Hand-rolled fake — the Core test project has no NSubstitute dependency.
    /// The repository now uses Guid primary keys matching the post-#1492
    /// <see cref="IUnitMembershipRepository"/> interface.
    /// </summary>
    private sealed class FakeMembershipRepository : IUnitMembershipRepository
    {
        private readonly List<UnitMembership> _rows = new();

        public static FakeMembershipRepository With(params (Guid unit, Guid agent)[] rows)
        {
            var repo = new FakeMembershipRepository();
            foreach (var (unit, agent) in rows)
            {
                repo._rows.Add(new UnitMembership(unit, agent));
            }
            return repo;
        }

        public Task UpsertAsync(UnitMembership membership, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid unitId, Guid agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAllForAgentAsync(Guid agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UnitMembership?> GetAsync(Guid unitId, Guid agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<UnitMembership>> ListByUnitAsync(Guid unitId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnitMembership>>(
                _rows.Where(r => r.UnitId == unitId).ToList());

        public Task<IReadOnlyList<UnitMembership>> ListByAgentAsync(Guid agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnitMembership>>(
                _rows.Where(r => r.AgentId == agentId).ToList());

        public Task<IReadOnlyList<UnitMembership>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnitMembership>>(_rows.ToList());
    }

    private sealed class FakePolicyRepository : IUnitPolicyRepository
    {
        // Policy repo is keyed by unit Guid, matching what
        // DefaultUnitPolicyEnforcer passes: membership.UnitId.
        private readonly Dictionary<Guid, UnitPolicy> _rows = new();

        public static FakePolicyRepository With(params (Guid unit, UnitPolicy policy)[] rows)
        {
            var repo = new FakePolicyRepository();
            foreach (var (unit, policy) in rows)
            {
                repo._rows[unit] = policy;
            }
            return repo;
        }

        public Task<UnitPolicy> GetAsync(Guid unitId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.TryGetValue(unitId, out var p) ? p : UnitPolicy.Empty);

        public Task SetAsync(Guid unitId, UnitPolicy policy, CancellationToken cancellationToken = default)
        {
            _rows[unitId] = policy;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid unitId, CancellationToken cancellationToken = default)
        {
            _rows.Remove(unitId);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Hand-rolled <see cref="ICostQueryService"/> fake. Seeded by tests with
    /// deterministic per-agent sums for the hourly / daily windows; returns
    /// empty summaries for agents the test did not configure.
    /// </summary>
    private sealed class FakeCostQueryService : ICostQueryService
    {
        private readonly Dictionary<Guid, decimal> _hourly = new();
        private readonly Dictionary<Guid, decimal> _daily = new();

        public void SetHourlyCost(string agentId, decimal cost) =>
            _hourly[Guid.Parse(agentId)] = cost;
        public void SetDailyCost(string agentId, decimal cost) =>
            _daily[Guid.Parse(agentId)] = cost;

        public Task<CostSummary> GetAgentCostAsync(Guid agentId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            var windowHours = (to - from).TotalHours;
            var total = windowHours <= 1.5
                ? (_hourly.TryGetValue(agentId, out var h) ? h : 0m)
                : (_daily.TryGetValue(agentId, out var d) ? d : 0m);

            return Task.FromResult(new CostSummary(
                TotalCost: total,
                TotalInputTokens: 0,
                TotalOutputTokens: 0,
                RecordCount: 0,
                WorkCost: total,
                InitiativeCost: 0m,
                From: from,
                To: to));
        }

        public Task<CostSummary> GetUnitCostAsync(Guid unitId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CostSummary(0m, 0, 0, 0, 0m, 0m, from, to));

        public Task<CostSummary> GetTenantCostAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CostSummary(0m, 0, 0, 0, 0m, 0m, from, to));

        public Task<CostTimeseries> GetTenantCostTimeseriesAsync(
            Guid tenantId,
            DateTimeOffset from,
            DateTimeOffset to,
            TimeSpan bucket,
            string bucketLabel,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CostTimeseries(from, to, bucketLabel, Array.Empty<CostTimeseriesBucket>()));

        public Task<CostTimeseries> GetAgentCostTimeseriesAsync(
            Guid agentId,
            DateTimeOffset from,
            DateTimeOffset to,
            TimeSpan bucket,
            string bucketLabel,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CostTimeseries(from, to, bucketLabel, Array.Empty<CostTimeseriesBucket>()));

        public Task<CostTimeseries> GetUnitCostTimeseriesAsync(
            Guid unitId,
            DateTimeOffset from,
            DateTimeOffset to,
            TimeSpan bucket,
            string bucketLabel,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CostTimeseries(from, to, bucketLabel, Array.Empty<CostTimeseriesBucket>()));

        public Task<IReadOnlyList<CostBreakdownEntry>> GetAgentCostBreakdownAsync(
            Guid agentId,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CostBreakdownEntry>>(Array.Empty<CostBreakdownEntry>());
    }
}