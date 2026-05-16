// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Memory;

using Cvoya.Spring.Core.Memory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Memory;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// EF-backed tests for <see cref="EfMemoryStore"/> (#2342). Exercise the
/// CRUD + search + tenant- / owner-isolation contract against the EF
/// in-memory provider. The full-text search path falls back to
/// case-insensitive substring matching on the in-memory provider; the
/// integration suite exercises the real Postgres FTS query.
/// </summary>
public class EfMemoryStoreTests : IDisposable
{
    private static readonly Guid TenantA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = new("aaaaaaaa-0000-0000-0000-000000000002");

    private static readonly Address AgentA = new(Address.AgentScheme, new Guid("bbbbbbbb-0000-0000-0000-000000000001"));
    private static readonly Address AgentB = new(Address.AgentScheme, new Guid("bbbbbbbb-0000-0000-0000-000000000002"));
    private static readonly Address UnitA = new(Address.UnitScheme, new Guid("cccccccc-0000-0000-0000-000000000001"));

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly ServiceProvider _providerA;
    private readonly ServiceProvider _providerB;

    public EfMemoryStoreTests()
    {
        _providerA = BuildProvider(TenantA);
        _providerB = BuildProvider(TenantB);
    }

    private ServiceProvider BuildProvider(Guid tenantId)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(tenantId));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddSingleton(TimeProvider.System);
        return services.BuildServiceProvider();
    }

    private EfMemoryStore CreateStore(ServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryStore>.Instance);

    public void Dispose()
    {
        _providerA.Dispose();
        _providerB.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AddAsync_LongTerm_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);

        var entry = await store.AddAsync(
            AgentA, MemoryKind.LongTerm, "remember this", source: "msg-123",
            threadId: null, topicIds: Array.Empty<Guid>(), ct);

        entry.Id.ShouldNotBe(Guid.Empty);
        entry.Owner.ShouldBe(AgentA);
        entry.Kind.ShouldBe(MemoryKind.LongTerm);
        entry.Content.ShouldBe("remember this");
        entry.Source.ShouldBe("msg-123");
        entry.ThreadId.ShouldBeNull();
        entry.TopicIds.ShouldBeEmpty();

        var fetched = await store.GetAsync(AgentA, entry.Id, ct);
        fetched.ShouldNotBeNull();
        fetched!.Content.ShouldBe("remember this");
    }

    [Fact]
    public async Task AddAsync_ShortTerm_RequiresThreadId()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await store.AddAsync(
                AgentA, MemoryKind.ShortTerm, "x", source: null,
                threadId: null, topicIds: Array.Empty<Guid>(), ct));
    }

    [Fact]
    public async Task AddAsync_ShortTerm_PersistsThreadId()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var thread = Guid.NewGuid();

        var entry = await store.AddAsync(
            AgentA, MemoryKind.ShortTerm, "working note", source: null,
            threadId: thread, topicIds: Array.Empty<Guid>(), ct);

        entry.ThreadId.ShouldBe(thread);
    }

    [Fact]
    public async Task GetAsync_OtherOwner_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var entry = await store.AddAsync(
            AgentA, MemoryKind.LongTerm, "a's memory", source: null,
            threadId: null, topicIds: Array.Empty<Guid>(), ct);

        var crossOwner = await store.GetAsync(AgentB, entry.Id, ct);
        crossOwner.ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_FiltersByKindAndTopicId()
    {
        var ct = TestContext.Current.CancellationToken;
        var topicStore = new EfMemoryTopicStore(
            _providerA.GetRequiredService<IServiceScopeFactory>(),
            _providerA.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryTopicStore>.Instance);
        var store = CreateStore(_providerA);
        var topic = await topicStore.AddAsync(AgentA, "design", null, ct);

        await store.AddAsync(AgentA, MemoryKind.LongTerm, "L1", null, null, Array.Empty<Guid>(), ct);
        await store.AddAsync(AgentA, MemoryKind.LongTerm, "L2", null, null, new[] { topic.Id }, ct);
        await store.AddAsync(AgentA, MemoryKind.ShortTerm, "S1", null, Guid.NewGuid(), Array.Empty<Guid>(), ct);

        var allLong = await store.ListAsync(AgentA, MemoryKind.LongTerm, null, 10, 0, ct);
        allLong.Count.ShouldBe(2);

        var byTopic = await store.ListAsync(AgentA, null, topic.Id, 10, 0, ct);
        byTopic.Count.ShouldBe(1);
        byTopic[0].Content.ShouldBe("L2");

        var allShort = await store.ListAsync(AgentA, MemoryKind.ShortTerm, null, 10, 0, ct);
        allShort.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SearchAsync_SubstringMatch_ReturnsHit()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);

        await store.AddAsync(AgentA, MemoryKind.LongTerm,
            "the quick brown fox jumps over the lazy dog", null, null, Array.Empty<Guid>(), ct);
        await store.AddAsync(AgentA, MemoryKind.LongTerm,
            "completely unrelated content", null, null, Array.Empty<Guid>(), ct);

        var hits = await store.SearchAsync(AgentA, "brown fox", null, null, 5, ct);
        hits.Count.ShouldBe(1);
        hits[0].Content.ShouldContain("brown fox");
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        await store.AddAsync(AgentA, MemoryKind.LongTerm, "x", null, null, Array.Empty<Guid>(), ct);

        var hits = await store.SearchAsync(AgentA, "  ", null, null, 5, ct);
        hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_RewritesContentAndTopicLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        var topicStore = new EfMemoryTopicStore(
            _providerA.GetRequiredService<IServiceScopeFactory>(),
            _providerA.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryTopicStore>.Instance);
        var store = CreateStore(_providerA);
        var initialTopic = await topicStore.AddAsync(AgentA, "alpha", null, ct);
        var replacementTopic = await topicStore.AddAsync(AgentA, "beta", null, ct);
        var entry = await store.AddAsync(AgentA, MemoryKind.LongTerm, "old content",
            null, null, new[] { initialTopic.Id }, ct);

        var updated = await store.UpdateAsync(AgentA, entry.Id,
            "new content", new[] { replacementTopic.Id }, ct);
        updated.ShouldNotBeNull();
        updated!.Content.ShouldBe("new content");
        updated.TopicIds.ShouldContain(replacementTopic.Id);
        updated.TopicIds.ShouldNotContain(initialTopic.Id);
    }

    [Fact]
    public async Task UpdateAsync_PartialContentOnly_LeavesTopicsIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var topicStore = new EfMemoryTopicStore(
            _providerA.GetRequiredService<IServiceScopeFactory>(),
            _providerA.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryTopicStore>.Instance);
        var store = CreateStore(_providerA);
        var topic = await topicStore.AddAsync(AgentA, "keep-me", null, ct);
        var entry = await store.AddAsync(AgentA, MemoryKind.LongTerm, "x", null, null, new[] { topic.Id }, ct);

        var updated = await store.UpdateAsync(AgentA, entry.Id, "y", null, ct);
        updated.ShouldNotBeNull();
        updated!.Content.ShouldBe("y");
        updated.TopicIds.ShouldContain(topic.Id);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRowAndLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        var topicStore = new EfMemoryTopicStore(
            _providerA.GetRequiredService<IServiceScopeFactory>(),
            _providerA.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryTopicStore>.Instance);
        var store = CreateStore(_providerA);
        var topic = await topicStore.AddAsync(AgentA, "alpha", null, ct);
        var entry = await store.AddAsync(AgentA, MemoryKind.LongTerm, "x", null, null, new[] { topic.Id }, ct);

        var deleted = await store.DeleteAsync(AgentA, entry.Id, ct);
        deleted.ShouldBeTrue();

        (await store.GetAsync(AgentA, entry.Id, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_OtherOwner_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var entry = await store.AddAsync(AgentA, MemoryKind.LongTerm, "x", null, null, Array.Empty<Guid>(), ct);

        var deleted = await store.DeleteAsync(AgentB, entry.Id, ct);
        deleted.ShouldBeFalse();

        (await store.GetAsync(AgentA, entry.Id, ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task TenantIsolation_TenantBCannotSeeTenantAEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        var storeA = CreateStore(_providerA);
        var storeB = CreateStore(_providerB);

        var entry = await storeA.AddAsync(AgentA, MemoryKind.LongTerm, "secret", null, null, Array.Empty<Guid>(), ct);

        // Tenant B sees nothing for the same agent address.
        (await storeB.GetAsync(AgentA, entry.Id, ct)).ShouldBeNull();
        var listed = await storeB.ListAsync(AgentA, null, null, 10, 0, ct);
        listed.ShouldBeEmpty();
    }

    [Fact]
    public async Task OwnerIsolation_AgentAandUnitADoNotShareMemories()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        await store.AddAsync(AgentA, MemoryKind.LongTerm, "agent secret", null, null, Array.Empty<Guid>(), ct);
        await store.AddAsync(UnitA, MemoryKind.LongTerm, "unit secret", null, null, Array.Empty<Guid>(), ct);

        var agentList = await store.ListAsync(AgentA, null, null, 10, 0, ct);
        agentList.Count.ShouldBe(1);
        agentList[0].Content.ShouldBe("agent secret");

        var unitList = await store.ListAsync(UnitA, null, null, 10, 0, ct);
        unitList.Count.ShouldBe(1);
        unitList[0].Content.ShouldBe("unit secret");
    }
}
