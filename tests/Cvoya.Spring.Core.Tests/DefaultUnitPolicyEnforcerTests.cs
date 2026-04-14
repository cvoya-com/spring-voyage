// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests;

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
    [Fact]
    public async Task EvaluateSkillInvocation_NoMemberships_Allowed()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            new FakeMembershipRepository(),
            new FakePolicyRepository());

        var result = await enforcer.EvaluateSkillInvocationAsync("ada", "search", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateSkillInvocation_UnitWithNoPolicy_Allowed()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With(("engineering", "ada")),
            new FakePolicyRepository());

        var result = await enforcer.EvaluateSkillInvocationAsync("ada", "search", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateSkillInvocation_UnitWithEmptyPolicy_Allowed()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With(("engineering", "ada")),
            FakePolicyRepository.With(("engineering", UnitPolicy.Empty)));

        var result = await enforcer.EvaluateSkillInvocationAsync("ada", "search", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateSkillInvocation_ToolInBlockList_Denied()
    {
        var policy = new UnitPolicy(new SkillPolicy(Blocked: new[] { "delete_repo" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With(("engineering", "ada")),
            FakePolicyRepository.With(("engineering", policy)));

        var result = await enforcer.EvaluateSkillInvocationAsync("ada", "delete_repo", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.DenyingUnitId.ShouldBe("engineering");
        result.Reason!.ShouldContain("blocked");
    }

    [Fact]
    public async Task EvaluateSkillInvocation_ToolNotInWhitelist_Denied()
    {
        var policy = new UnitPolicy(new SkillPolicy(Allowed: new[] { "search" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With(("engineering", "ada")),
            FakePolicyRepository.With(("engineering", policy)));

        var result = await enforcer.EvaluateSkillInvocationAsync("ada", "delete_repo", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.DenyingUnitId.ShouldBe("engineering");
    }

    [Fact]
    public async Task EvaluateSkillInvocation_ToolInWhitelist_Allowed()
    {
        var policy = new UnitPolicy(new SkillPolicy(Allowed: new[] { "search", "summarize" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With(("engineering", "ada")),
            FakePolicyRepository.With(("engineering", policy)));

        var result = await enforcer.EvaluateSkillInvocationAsync("ada", "summarize", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task EvaluateSkillInvocation_BlockPrecedesAllow_Denied()
    {
        var policy = new UnitPolicy(new SkillPolicy(
            Allowed: new[] { "search", "delete_repo" },
            Blocked: new[] { "delete_repo" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With(("engineering", "ada")),
            FakePolicyRepository.With(("engineering", policy)));

        var result = await enforcer.EvaluateSkillInvocationAsync("ada", "delete_repo", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.Reason!.ShouldContain("blocked");
    }

    [Fact]
    public async Task EvaluateSkillInvocation_CaseInsensitiveMatch()
    {
        var policy = new UnitPolicy(new SkillPolicy(Blocked: new[] { "Delete_Repo" }));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With(("engineering", "ada")),
            FakePolicyRepository.With(("engineering", policy)));

        var result = await enforcer.EvaluateSkillInvocationAsync("ada", "DELETE_REPO", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task EvaluateSkillInvocation_EmptyAllowedList_DeniesEverything()
    {
        // Allowed: [] is a legitimate "disable every tool" state, distinct
        // from Allowed: null which means "no whitelist constraint".
        var policy = new UnitPolicy(new SkillPolicy(Allowed: Array.Empty<string>()));
        var enforcer = new DefaultUnitPolicyEnforcer(
            FakeMembershipRepository.With(("engineering", "ada")),
            FakePolicyRepository.With(("engineering", policy)));

        var result = await enforcer.EvaluateSkillInvocationAsync("ada", "search", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task EvaluateSkillInvocation_MultipleUnits_FirstDenyingUnitWins()
    {
        // Agent belongs to two units — marketing is permissive, engineering
        // blocks the tool. Either iteration order is legal; the test asserts
        // that SOME denying unit is identified.
        var memberships = FakeMembershipRepository.With(
            ("marketing", "ada"),
            ("engineering", "ada"));
        var policies = FakePolicyRepository.With(
            ("marketing", UnitPolicy.Empty),
            ("engineering", new UnitPolicy(new SkillPolicy(Blocked: new[] { "delete_repo" }))));

        var enforcer = new DefaultUnitPolicyEnforcer(memberships, policies);

        var result = await enforcer.EvaluateSkillInvocationAsync("ada", "delete_repo", TestContext.Current.CancellationToken);

        result.IsAllowed.ShouldBeFalse();
        result.DenyingUnitId.ShouldBe("engineering");
    }

    [Fact]
    public async Task EvaluateSkillInvocation_EmptyAgentOrTool_Allowed()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            new FakeMembershipRepository(),
            new FakePolicyRepository());

        var ct = TestContext.Current.CancellationToken;
        (await enforcer.EvaluateSkillInvocationAsync("", "search", ct))
            .IsAllowed.ShouldBeTrue();
        (await enforcer.EvaluateSkillInvocationAsync("ada", "", ct))
            .IsAllowed.ShouldBeTrue();
    }

    /// <summary>
    /// Hand-rolled fake — the Core test project has no NSubstitute dependency.
    /// </summary>
    private sealed class FakeMembershipRepository : IUnitMembershipRepository
    {
        private readonly List<UnitMembership> _rows = new();

        public static FakeMembershipRepository With(params (string unit, string agent)[] rows)
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

        public Task DeleteAsync(string unitId, string agentAddress, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UnitMembership?> GetAsync(string unitId, string agentAddress, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<UnitMembership>> ListByUnitAsync(string unitId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnitMembership>>(
                _rows.Where(r => r.UnitId == unitId).ToList());

        public Task<IReadOnlyList<UnitMembership>> ListByAgentAsync(string agentAddress, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnitMembership>>(
                _rows.Where(r => r.AgentAddress == agentAddress).ToList());
    }

    private sealed class FakePolicyRepository : IUnitPolicyRepository
    {
        private readonly Dictionary<string, UnitPolicy> _rows = new(StringComparer.Ordinal);

        public static FakePolicyRepository With(params (string unit, UnitPolicy policy)[] rows)
        {
            var repo = new FakePolicyRepository();
            foreach (var (unit, policy) in rows)
            {
                repo._rows[unit] = policy;
            }
            return repo;
        }

        public Task<UnitPolicy> GetAsync(string unitId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.TryGetValue(unitId, out var p) ? p : UnitPolicy.Empty);

        public Task SetAsync(string unitId, UnitPolicy policy, CancellationToken cancellationToken = default)
        {
            _rows[unitId] = policy;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string unitId, CancellationToken cancellationToken = default)
        {
            _rows.Remove(unitId);
            return Task.CompletedTask;
        }
    }
}