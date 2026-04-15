// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitPolicyRepository"/>: default empty policy,
/// upsert with and without skill sub-record, re-read round-trip, delete,
/// and the "empty policy is a delete" rule.
/// </summary>
public class UnitPolicyRepositoryTests : IDisposable
{
    private readonly SpringDbContext _context;
    private readonly UnitPolicyRepository _repository;

    public UnitPolicyRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new SpringDbContext(options);
        _repository = new UnitPolicyRepository(_context);
    }

    [Fact]
    public async Task GetAsync_NoRow_ReturnsEmptyPolicy()
    {
        var ct = TestContext.Current.CancellationToken;

        var policy = await _repository.GetAsync("engineering", ct);

        policy.ShouldBe(UnitPolicy.Empty);
        policy.Skill.ShouldBeNull();
    }

    [Fact]
    public async Task SetAsync_WithSkillPolicy_PersistsAndRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var policy = new UnitPolicy(new SkillPolicy(
            Allowed: new[] { "search", "summarize" },
            Blocked: new[] { "delete_repo" }));

        await _repository.SetAsync("engineering", policy, ct);
        var stored = await _repository.GetAsync("engineering", ct);

        stored.Skill.ShouldNotBeNull();
        stored.Skill!.Allowed.ShouldBe(new[] { "search", "summarize" });
        stored.Skill.Blocked.ShouldBe(new[] { "delete_repo" });
    }

    [Fact]
    public async Task SetAsync_OverwritesExistingRow()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.SetAsync(
            "engineering",
            new UnitPolicy(new SkillPolicy(Blocked: new[] { "old" })),
            ct);

        await _repository.SetAsync(
            "engineering",
            new UnitPolicy(new SkillPolicy(Blocked: new[] { "new" })),
            ct);

        var stored = await _repository.GetAsync("engineering", ct);
        stored.Skill!.Blocked.ShouldBe(new[] { "new" });
    }

    [Fact]
    public async Task SetAsync_EmptyPolicy_DeletesRow()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.SetAsync(
            "engineering",
            new UnitPolicy(new SkillPolicy(Blocked: new[] { "x" })),
            ct);

        await _repository.SetAsync("engineering", UnitPolicy.Empty, ct);

        // No row — the GetAsync contract returns Empty when no row exists.
        var stored = await _repository.GetAsync("engineering", ct);
        stored.ShouldBe(UnitPolicy.Empty);
        (await _context.UnitPolicies.CountAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.SetAsync(
            "engineering",
            new UnitPolicy(new SkillPolicy(Allowed: new[] { "search" })),
            ct);

        await _repository.DeleteAsync("engineering", ct);

        var stored = await _repository.GetAsync("engineering", ct);
        stored.ShouldBe(UnitPolicy.Empty);
    }

    [Fact]
    public async Task SetAsync_AllDimensions_RoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var policy = new UnitPolicy(
            Skill: new SkillPolicy(Allowed: new[] { "search" }),
            Model: new ModelPolicy(Blocked: new[] { "gpt-4" }),
            Cost: new CostPolicy(MaxCostPerInvocation: 0.1m, MaxCostPerDay: 5m),
            ExecutionMode: new ExecutionModePolicy(Forced: AgentExecutionMode.OnDemand),
            Initiative: new InitiativePolicy(BlockedActions: new[] { "delete-repo" }));

        await _repository.SetAsync("engineering", policy, ct);
        var stored = await _repository.GetAsync("engineering", ct);

        stored.Skill!.Allowed.ShouldBe(new[] { "search" });
        stored.Model!.Blocked.ShouldBe(new[] { "gpt-4" });
        stored.Cost!.MaxCostPerInvocation.ShouldBe(0.1m);
        stored.Cost.MaxCostPerDay.ShouldBe(5m);
        stored.ExecutionMode!.Forced.ShouldBe(AgentExecutionMode.OnDemand);
        stored.Initiative!.BlockedActions.ShouldBe(new[] { "delete-repo" });
    }

    [Fact]
    public async Task SetAsync_ClearsDimensionOnOverwrite()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.SetAsync(
            "engineering",
            new UnitPolicy(
                Skill: new SkillPolicy(Blocked: new[] { "x" }),
                Model: new ModelPolicy(Blocked: new[] { "gpt-4" })),
            ct);

        // Overwrite with only skill — the model column should be cleared.
        await _repository.SetAsync(
            "engineering",
            new UnitPolicy(Skill: new SkillPolicy(Blocked: new[] { "x" })),
            ct);

        var stored = await _repository.GetAsync("engineering", ct);
        stored.Skill.ShouldNotBeNull();
        stored.Model.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_NoRow_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.DeleteAsync("ghost", ct);

        (await _context.UnitPolicies.CountAsync(ct)).ShouldBe(0);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}