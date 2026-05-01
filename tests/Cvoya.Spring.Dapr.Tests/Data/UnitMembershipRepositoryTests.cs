// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitMembershipRepository"/>: upsert semantics,
/// get / list access paths, and delete behavior. The in-memory provider
/// does not enforce database defaults, so fields are explicitly supplied
/// in these tests.
///
/// All primary keys are stable UUIDs (post #1492 migration). Stable
/// constants are defined below so every test shares the same identities
/// and the intent is readable without UUID strings inline.
/// </summary>
public class UnitMembershipRepositoryTests : IDisposable
{
    // Agents
    private static readonly Guid AgentAda = new("aadaadaa-0000-0000-0000-000000000001");
    private static readonly Guid AgentHopper = new("aadaadaa-0000-0000-0000-000000000002");

    // Units — named so their lexicographic UUID order matches intent.
    // aaaa... < bbbb... < cccc... preserves the "alpha < marketing < zeta" tiebreaker test.
    private static readonly Guid UnitAlpha = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UnitEngineering = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid UnitMarketing = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid UnitSales = new("bbbbbbbb-0000-0000-0000-000000000003");
    private static readonly Guid UnitZeta = new("cccccccc-0000-0000-0000-000000000001");

    private readonly SpringDbContext _context;
    private readonly UnitMembershipRepository _repository;

    public UnitMembershipRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new SpringDbContext(options);
        _repository = new UnitMembershipRepository(_context);
    }

    [Fact]
    public async Task UpsertAsync_NewRow_CreatesAndStampsTimestamps()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(
            new UnitMembership(UnitEngineering, AgentAda, Enabled: true),
            ct);

        var persisted = await _repository.GetAsync(UnitEngineering, AgentAda, ct);
        persisted.ShouldNotBeNull();
        persisted!.UnitId.ShouldBe(UnitEngineering);
        persisted.AgentId.ShouldBe(AgentAda);
        persisted.Enabled.ShouldBeTrue();
        persisted.CreatedAt.ShouldNotBe(default);
        persisted.UpdatedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task UpsertAsync_ExistingRow_UpdatesOverrides()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(
            new UnitMembership(UnitEngineering, AgentAda, Enabled: true),
            ct);
        var created = await _repository.GetAsync(UnitEngineering, AgentAda, ct);

        await _repository.UpsertAsync(
            new UnitMembership(UnitEngineering, AgentAda,
                Model: "claude-opus",
                Specialty: "reviewer",
                Enabled: false,
                ExecutionMode: AgentExecutionMode.OnDemand),
            ct);

        var updated = await _repository.GetAsync(UnitEngineering, AgentAda, ct);
        updated.ShouldNotBeNull();
        updated!.Model.ShouldBe("claude-opus");
        updated.Specialty.ShouldBe("reviewer");
        updated.Enabled.ShouldBeFalse();
        updated.ExecutionMode.ShouldBe(AgentExecutionMode.OnDemand);
        // CreatedAt must not move on update.
        updated.CreatedAt.ShouldBe(created!.CreatedAt);
    }

    [Fact]
    public async Task GetAsync_MissingRow_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        var ghost = Guid.NewGuid();
        var result = await _repository.GetAsync(ghost, ghost, ct);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ListByUnitAsync_ReturnsOnlyMatchingRows()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(new UnitMembership(UnitEngineering, AgentAda), ct);
        await _repository.UpsertAsync(new UnitMembership(UnitEngineering, AgentHopper), ct);
        await _repository.UpsertAsync(new UnitMembership(UnitMarketing, AgentAda), ct);

        var list = await _repository.ListByUnitAsync(UnitEngineering, ct);
        list.Count.ShouldBe(2);
        list.ShouldAllBe(m => m.UnitId == UnitEngineering);
    }

    [Fact]
    public async Task ListByAgentAsync_ReturnsEveryMembershipInCreatedOrder()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(new UnitMembership(UnitEngineering, AgentAda), ct);
        await Task.Delay(10, ct);
        await _repository.UpsertAsync(new UnitMembership(UnitMarketing, AgentAda), ct);

        var list = await _repository.ListByAgentAsync(AgentAda, ct);
        list.Count.ShouldBe(2);
        list[0].UnitId.ShouldBe(UnitEngineering);
        list[1].UnitId.ShouldBe(UnitMarketing);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;

        // Two memberships for the same agent so the #744 last-membership
        // guard does not trip when we drop one of them.
        await _repository.UpsertAsync(new UnitMembership(UnitEngineering, AgentAda), ct);
        await _repository.UpsertAsync(new UnitMembership(UnitMarketing, AgentAda), ct);
        await _repository.DeleteAsync(UnitEngineering, AgentAda, ct);

        (await _repository.GetAsync(UnitEngineering, AgentAda, ct)).ShouldBeNull();
        (await _repository.GetAsync(UnitMarketing, AgentAda, ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_MissingRow_IsNoop()
    {
        var ct = TestContext.Current.CancellationToken;

        var ghost = Guid.NewGuid();
        // Should not throw.
        await _repository.DeleteAsync(ghost, ghost, ct);
    }

    [Fact]
    public async Task DeleteAsync_LastMembership_Throws()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(new UnitMembership(UnitEngineering, AgentAda), ct);

        var ex = await Should.ThrowAsync<AgentMembershipRequiredException>(
            () => _repository.DeleteAsync(UnitEngineering, AgentAda, ct));
        ex.AgentId.ShouldBe(AgentAda);
        ex.UnitId.ShouldBe(UnitEngineering);

        // Row must still exist — the invariant is enforced as a transactional rejection.
        (await _repository.GetAsync(UnitEngineering, AgentAda, ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteAllForAgentAsync_BypassesLastMembershipGuard()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(new UnitMembership(UnitEngineering, AgentAda), ct);
        await _repository.UpsertAsync(new UnitMembership(UnitMarketing, AgentAda), ct);

        await _repository.DeleteAllForAgentAsync(AgentAda, ct);

        (await _repository.ListByAgentAsync(AgentAda, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteAllForAgentAsync_NoMemberships_Noop()
    {
        var ct = TestContext.Current.CancellationToken;

        var ghost = Guid.NewGuid();
        // Should not throw.
        await _repository.DeleteAllForAgentAsync(ghost, ct);
    }

    [Fact]
    public async Task UpsertAsync_FirstMembershipForAgent_MarkedPrimary()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(new UnitMembership(UnitEngineering, AgentAda), ct);

        var persisted = await _repository.GetAsync(UnitEngineering, AgentAda, ct);
        persisted!.IsPrimary.ShouldBeTrue();
    }

    [Fact]
    public async Task UpsertAsync_SecondMembershipForAgent_NotPrimary()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(new UnitMembership(UnitEngineering, AgentAda), ct);
        await _repository.UpsertAsync(new UnitMembership(UnitMarketing, AgentAda), ct);

        var first = await _repository.GetAsync(UnitEngineering, AgentAda, ct);
        var second = await _repository.GetAsync(UnitMarketing, AgentAda, ct);
        first!.IsPrimary.ShouldBeTrue();
        second!.IsPrimary.ShouldBeFalse();
    }

    [Fact]
    public async Task UpsertAsync_UpdateExistingRow_PreservesIsPrimary()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(new UnitMembership(UnitEngineering, AgentAda), ct);
        await _repository.UpsertAsync(
            new UnitMembership(UnitEngineering, AgentAda, Model: "claude-opus"), ct);

        var persisted = await _repository.GetAsync(UnitEngineering, AgentAda, ct);
        persisted!.IsPrimary.ShouldBeTrue();
        persisted.Model.ShouldBe("claude-opus");
    }

    [Fact]
    public async Task DeleteAsync_PrimaryMembership_PromotesOldestSurvivor()
    {
        var ct = TestContext.Current.CancellationToken;

        // Engineering is inserted first → becomes primary. Marketing + Sales
        // are non-primary. Delete Engineering → oldest survivor (Marketing)
        // should be promoted to primary; Sales unchanged.
        await _repository.UpsertAsync(new UnitMembership(UnitEngineering, AgentAda), ct);
        await Task.Delay(10, ct);
        await _repository.UpsertAsync(new UnitMembership(UnitMarketing, AgentAda), ct);
        await Task.Delay(10, ct);
        await _repository.UpsertAsync(new UnitMembership(UnitSales, AgentAda), ct);

        await _repository.DeleteAsync(UnitEngineering, AgentAda, ct);

        var marketing = await _repository.GetAsync(UnitMarketing, AgentAda, ct);
        var sales = await _repository.GetAsync(UnitSales, AgentAda, ct);
        marketing!.IsPrimary.ShouldBeTrue();
        sales!.IsPrimary.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteAsync_PrimaryMembership_TiebreaksByUnitIdLex()
    {
        var ct = TestContext.Current.CancellationToken;

        // Zeta (primary — first insert), then Marketing + Alpha created at the
        // same logical time (no delay). When Zeta is removed, the tiebreaker
        // should pick Alpha (UUID lex < Marketing UUID), not Marketing.
        // UUID ordering: UnitAlpha (aaaa...) < UnitMarketing (bbbb...*02) < UnitZeta (cccc...).
        await _repository.UpsertAsync(new UnitMembership(UnitZeta, AgentAda), ct);
        await _repository.UpsertAsync(new UnitMembership(UnitMarketing, AgentAda), ct);
        await _repository.UpsertAsync(new UnitMembership(UnitAlpha, AgentAda), ct);

        // Force identical CreatedAt on the two survivors so the unit-id
        // tiebreaker is the only deciding signal.
        var now = DateTimeOffset.UtcNow;
        foreach (var row in await _context.UnitMemberships
            .Where(m => m.AgentId == AgentAda && m.UnitId != UnitZeta)
            .ToListAsync(ct))
        {
            row.CreatedAt = now;
        }
        await _context.SaveChangesAsync(ct);

        await _repository.DeleteAsync(UnitZeta, AgentAda, ct);

        var alpha = await _repository.GetAsync(UnitAlpha, AgentAda, ct);
        var marketing = await _repository.GetAsync(UnitMarketing, AgentAda, ct);
        alpha!.IsPrimary.ShouldBeTrue();
        marketing!.IsPrimary.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteAsync_NonPrimaryMembership_PrimaryUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(new UnitMembership(UnitEngineering, AgentAda), ct);
        await _repository.UpsertAsync(new UnitMembership(UnitMarketing, AgentAda), ct);

        await _repository.DeleteAsync(UnitMarketing, AgentAda, ct);

        var engineering = await _repository.GetAsync(UnitEngineering, AgentAda, ct);
        engineering!.IsPrimary.ShouldBeTrue();
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}