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
/// </summary>
public class UnitMembershipRepositoryTests : IDisposable
{
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
            new UnitMembership("engineering", "ada", Enabled: true),
            ct);

        var persisted = await _repository.GetAsync("engineering", "ada", ct);
        persisted.ShouldNotBeNull();
        persisted!.UnitId.ShouldBe("engineering");
        persisted.AgentAddress.ShouldBe("ada");
        persisted.Enabled.ShouldBeTrue();
        persisted.CreatedAt.ShouldNotBe(default);
        persisted.UpdatedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task UpsertAsync_ExistingRow_UpdatesOverrides()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(
            new UnitMembership("engineering", "ada", Enabled: true),
            ct);
        var created = await _repository.GetAsync("engineering", "ada", ct);

        await _repository.UpsertAsync(
            new UnitMembership("engineering", "ada",
                Model: "claude-opus",
                Specialty: "reviewer",
                Enabled: false,
                ExecutionMode: AgentExecutionMode.OnDemand),
            ct);

        var updated = await _repository.GetAsync("engineering", "ada", ct);
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

        var result = await _repository.GetAsync("ghost", "ghost", ct);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ListByUnitAsync_ReturnsOnlyMatchingRows()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(new UnitMembership("engineering", "ada"), ct);
        await _repository.UpsertAsync(new UnitMembership("engineering", "hopper"), ct);
        await _repository.UpsertAsync(new UnitMembership("marketing", "ada"), ct);

        var list = await _repository.ListByUnitAsync("engineering", ct);
        list.Count.ShouldBe(2);
        list.ShouldAllBe(m => m.UnitId == "engineering");
    }

    [Fact]
    public async Task ListByAgentAsync_ReturnsEveryMembershipInCreatedOrder()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(new UnitMembership("engineering", "ada"), ct);
        await Task.Delay(10, ct);
        await _repository.UpsertAsync(new UnitMembership("marketing", "ada"), ct);

        var list = await _repository.ListByAgentAsync("ada", ct);
        list.Count.ShouldBe(2);
        list[0].UnitId.ShouldBe("engineering");
        list[1].UnitId.ShouldBe("marketing");
    }

    [Fact]
    public async Task DeleteAsync_RemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;

        // Two memberships for the same agent so the #744 last-membership
        // guard does not trip when we drop one of them.
        await _repository.UpsertAsync(new UnitMembership("engineering", "ada"), ct);
        await _repository.UpsertAsync(new UnitMembership("marketing", "ada"), ct);
        await _repository.DeleteAsync("engineering", "ada", ct);

        (await _repository.GetAsync("engineering", "ada", ct)).ShouldBeNull();
        (await _repository.GetAsync("marketing", "ada", ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_MissingRow_IsNoop()
    {
        var ct = TestContext.Current.CancellationToken;

        // Should not throw.
        await _repository.DeleteAsync("ghost", "ghost", ct);
    }

    [Fact]
    public async Task DeleteAsync_LastMembership_Throws()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(new UnitMembership("engineering", "ada"), ct);

        var ex = await Should.ThrowAsync<AgentMembershipRequiredException>(
            () => _repository.DeleteAsync("engineering", "ada", ct));
        ex.AgentAddress.ShouldBe("ada");
        ex.UnitId.ShouldBe("engineering");

        // Row must still exist — the invariant is enforced as a transactional rejection.
        (await _repository.GetAsync("engineering", "ada", ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteAllForAgentAsync_BypassesLastMembershipGuard()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertAsync(new UnitMembership("engineering", "ada"), ct);
        await _repository.UpsertAsync(new UnitMembership("marketing", "ada"), ct);

        await _repository.DeleteAllForAgentAsync("ada", ct);

        (await _repository.ListByAgentAsync("ada", ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteAllForAgentAsync_NoMemberships_Noop()
    {
        var ct = TestContext.Current.CancellationToken;

        // Should not throw.
        await _repository.DeleteAllForAgentAsync("ghost", ct);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}