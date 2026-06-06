// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Threads;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Dapr.Threads;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="EfThreadRegistry"/>: participant-set canonicalisation,
/// concurrent-insert convergence, tenant isolation, and resolve round-trip
/// (#2047 / ADR-0030 / ADR-0040).
/// </summary>
public class EfThreadRegistryTests : IDisposable
{
    private static readonly Guid Tenant1 = new("11111111-2222-3333-4444-000000000001");
    private static readonly Guid Tenant2 = new("11111111-2222-3333-4444-000000000002");

    private static readonly Address Human1 = new("human", new Guid("aaaa0001-0000-0000-0000-000000000001"));
    private static readonly Address Agent1 = new("agent", new Guid("aaaa0002-0000-0000-0000-000000000001"));
    private static readonly Address Agent2 = new("agent", new Guid("aaaa0002-0000-0000-0000-000000000002"));
    private static readonly Address Unit1 = new("unit", new Guid("aaaa0003-0000-0000-0000-000000000001"));

    private readonly DbContextOptions<SpringDbContext> _dbOptions;

    public EfThreadRegistryTests()
    {
        _dbOptions = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    [Fact]
    public async Task GetOrCreateAsync_SameParticipantSet_ReturnsSameThreadId()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var first = await sut.GetOrCreateAsync(new[] { Human1, Agent1 }, ct);
        var second = await sut.GetOrCreateAsync(new[] { Human1, Agent1 }, ct);

        first.ShouldNotBeNullOrWhiteSpace();
        Guid.TryParse(first, out _).ShouldBeTrue();
        second.ShouldBe(first);
    }

    [Fact]
    public async Task GetOrCreateAsync_OrderInvariant_ReturnsSameThreadId()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var ordered = await sut.GetOrCreateAsync(new[] { Human1, Agent1, Unit1 }, ct);
        var reversed = await sut.GetOrCreateAsync(new[] { Unit1, Human1, Agent1 }, ct);
        var withDuplicate = await sut.GetOrCreateAsync(
            new[] { Agent1, Human1, Unit1, Human1 }, ct);

        reversed.ShouldBe(ordered);
        withDuplicate.ShouldBe(ordered);
    }

    [Fact]
    public async Task GetOrCreateAsync_DifferentParticipantSets_ReturnDifferentThreadIds()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var pair1 = await sut.GetOrCreateAsync(new[] { Human1, Agent1 }, ct);
        var pair2 = await sut.GetOrCreateAsync(new[] { Human1, Agent2 }, ct);
        var triple = await sut.GetOrCreateAsync(new[] { Human1, Agent1, Unit1 }, ct);

        pair1.ShouldNotBe(pair2);
        pair1.ShouldNotBe(triple);
        pair2.ShouldNotBe(triple);
    }

    [Fact]
    public async Task GetOrCreateAsync_TenantIsolated_DifferentTenantsGetDifferentRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var t1 = NewRegistry(Tenant1);
        var t2 = NewRegistry(Tenant2);

        var t1Id = await t1.GetOrCreateAsync(new[] { Human1, Agent1 }, ct);
        var t2Id = await t2.GetOrCreateAsync(new[] { Human1, Agent1 }, ct);

        // Distinct rows, distinct ids — and tenant 2's row is not visible to
        // tenant 1's resolver.
        t1Id.ShouldNotBe(t2Id);

        var t1Resolves = await t1.ResolveAsync(t2Id, ct);
        t1Resolves.ShouldBeNull();

        var t2Resolves = await t2.ResolveAsync(t1Id, ct);
        t2Resolves.ShouldBeNull();
    }

    [Fact]
    public async Task GetOrCreateAsync_RaceLoserPath_ConvergesOnWinningRow()
    {
        // Direct exercise of the DbUpdateException race path. The in-memory
        // provider does not enforce the unique index, so we simulate the
        // race the production Postgres provider would surface: tenant-A
        // pre-inserts a row for the participant set, tenant-A then re-runs
        // GetOrCreate, which would race in production but in this harness
        // simply finds the existing row on the cache-hit branch. To exercise
        // the catch path explicitly, we ask the registry to insert a second
        // row whose unique-key collision is simulated by manually adding a
        // duplicate before the SaveChanges runs is not portable across EF
        // providers. This test instead asserts the post-conditions the
        // production race path must satisfy: an existing row is reused, and
        // the resolver returns the same id every caller observed.
        var ct = TestContext.Current.CancellationToken;
        var participants = new[] { Human1, Agent1 };

        var first = NewRegistry(Tenant1);
        var firstId = await first.GetOrCreateAsync(participants, ct);

        // A second concurrent caller (modeled as a fresh DbContext + registry
        // pair) must observe the same row.
        var second = NewRegistry(Tenant1);
        var secondId = await second.GetOrCreateAsync(participants, ct);

        secondId.ShouldBe(firstId);

        var verifier = NewRegistry(Tenant1);
        var resolved = await verifier.ResolveAsync(firstId, ct);
        resolved.ShouldNotBeNull();
        resolved!.Participants.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveAsync_KnownThreadId_ReturnsParticipants()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var threadId = await sut.GetOrCreateAsync(new[] { Human1, Agent1, Unit1 }, ct);
        var resolved = await sut.ResolveAsync(threadId, ct);

        resolved.ShouldNotBeNull();
        resolved!.ThreadId.ShouldBe(threadId);
        resolved.Participants.Count.ShouldBe(3);

        // Both no-dash and dashed inputs must resolve (lenient parsing per
        // CONVENTIONS.md § Identifiers).
        var dashedId = Guid.Parse(threadId).ToString();
        var resolvedDashed = await sut.ResolveAsync(dashedId, ct);
        resolvedDashed.ShouldNotBeNull();
        resolvedDashed!.ThreadId.ShouldBe(threadId);
    }

    [Fact]
    public async Task ResolveAsync_UnknownOrInvalid_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        (await sut.ResolveAsync("not-a-guid", ct)).ShouldBeNull();
        (await sut.ResolveAsync(string.Empty, ct)).ShouldBeNull();
        (await sut.ResolveAsync(GuidFormatter.Format(Guid.NewGuid()), ct)).ShouldBeNull();
    }

    [Fact]
    public async Task GetOrCreateAsync_EmptyParticipantSet_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await sut.GetOrCreateAsync(Array.Empty<Address>(), ct));
    }

    private EfThreadRegistry NewRegistry(Guid tenantId)
    {
        var tenantContext = new StaticTenantContext(tenantId);
        var db = new SpringDbContext(_dbOptions, tenantContext);
        return new EfThreadRegistry(db, tenantContext);
    }

    public void Dispose()
    {
        // Per-test in-memory database — nothing to dispose at the fixture
        // level; SpringDbContext instances are owned by NewRegistry and
        // disposed when the test finishes.
        GC.SuppressFinalize(this);
    }
}
