// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Memory;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Memory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Memory;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// EF-backed tests for <see cref="EfMemoryTopicStore"/> (#2342). Covers
/// the topic CRUD + owner uniqueness + cascade-on-delete contract, plus
/// the tenant-isolation invariant inherited from
/// <see cref="SpringDbContext"/>'s query filter.
/// </summary>
public class EfMemoryTopicStoreTests : IDisposable
{
    private static readonly Guid TenantA = new("aaaaaaaa-0000-0000-0000-000000000010");
    private static readonly Guid TenantB = new("aaaaaaaa-0000-0000-0000-000000000020");

    private static readonly Address AgentA = new(Address.AgentScheme, new Guid("bbbbbbbb-0000-0000-0000-000000000010"));
    private static readonly Address AgentB = new(Address.AgentScheme, new Guid("bbbbbbbb-0000-0000-0000-000000000020"));

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly ServiceProvider _providerA;
    private readonly ServiceProvider _providerB;

    public EfMemoryTopicStoreTests()
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

    private EfMemoryTopicStore CreateTopicStore(ServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryTopicStore>.Instance);

    private EfMemoryStore CreateMemoryStore(ServiceProvider provider) =>
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
    public async Task AddAsync_NewTopic_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateTopicStore(_providerA);

        var topic = await store.AddAsync(AgentA, "design-decisions", "where we record the why", ct);
        topic.Id.ShouldNotBe(Guid.Empty);
        topic.Name.ShouldBe("design-decisions");
        topic.Description.ShouldBe("where we record the why");

        var fetched = await store.GetAsync(AgentA, topic.Id, ct);
        fetched.ShouldNotBeNull();
        fetched!.Name.ShouldBe("design-decisions");
    }

    [Fact]
    public async Task AddAsync_DuplicateName_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateTopicStore(_providerA);
        await store.AddAsync(AgentA, "alpha", null, ct);

        await Should.ThrowAsync<SpringException>(async () =>
            await store.AddAsync(AgentA, "alpha", "second", ct));
    }

    [Fact]
    public async Task AddAsync_SameNameDifferentOwner_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateTopicStore(_providerA);

        var a = await store.AddAsync(AgentA, "alpha", null, ct);
        var b = await store.AddAsync(AgentB, "alpha", null, ct);
        a.Id.ShouldNotBe(b.Id);
    }

    [Fact]
    public async Task ListAsync_OrdersByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateTopicStore(_providerA);
        await store.AddAsync(AgentA, "beta", null, ct);
        await store.AddAsync(AgentA, "alpha", null, ct);
        await store.AddAsync(AgentA, "gamma", null, ct);

        var topics = await store.ListAsync(AgentA, 10, 0, ct);
        topics.Select(t => t.Name).ShouldBe(new[] { "alpha", "beta", "gamma" });
    }

    [Fact]
    public async Task SearchAsync_MatchesNameAndDescription()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateTopicStore(_providerA);
        await store.AddAsync(AgentA, "design", "where we record the why", ct);
        await store.AddAsync(AgentA, "operations", "runbook entries", ct);

        (await store.SearchAsync(AgentA, "design", 10, ct)).Count.ShouldBe(1);
        (await store.SearchAsync(AgentA, "runbook", 10, ct)).Count.ShouldBe(1);
        (await store.SearchAsync(AgentA, "where", 10, ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateAsync_PartialRename_LeavesDescriptionIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateTopicStore(_providerA);
        var topic = await store.AddAsync(AgentA, "old-name", "keep-me", ct);

        var updated = await store.UpdateAsync(AgentA, topic.Id, "new-name", null, ct);
        updated.ShouldNotBeNull();
        updated!.Name.ShouldBe("new-name");
        updated.Description.ShouldBe("keep-me");
    }

    [Fact]
    public async Task UpdateAsync_RenameCollision_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateTopicStore(_providerA);
        await store.AddAsync(AgentA, "alpha", null, ct);
        var beta = await store.AddAsync(AgentA, "beta", null, ct);

        await Should.ThrowAsync<SpringException>(async () =>
            await store.UpdateAsync(AgentA, beta.Id, "alpha", null, ct));
    }

    [Fact]
    public async Task DeleteAsync_CascadesLinksLeavesMemoriesIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var topicStore = CreateTopicStore(_providerA);
        var memoryStore = CreateMemoryStore(_providerA);

        var topic = await topicStore.AddAsync(AgentA, "alpha", null, ct);
        var entry = await memoryStore.AddAsync(
            AgentA, MemoryKind.LongTerm, "memory body", source: null,
            threadId: null, topicIds: new[] { topic.Id }, ct);

        var deleted = await topicStore.DeleteAsync(AgentA, topic.Id, ct);
        deleted.ShouldBeTrue();

        // The memory survives — only the topic-link row was removed.
        var fetched = await memoryStore.GetAsync(AgentA, entry.Id, ct);
        fetched.ShouldNotBeNull();
        fetched!.TopicIds.ShouldBeEmpty();

        // The topic itself is gone.
        (await topicStore.GetAsync(AgentA, topic.Id, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_OtherOwner_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateTopicStore(_providerA);
        var topic = await store.AddAsync(AgentA, "alpha", null, ct);

        var deleted = await store.DeleteAsync(AgentB, topic.Id, ct);
        deleted.ShouldBeFalse();
        (await store.GetAsync(AgentA, topic.Id, ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task TenantIsolation_TenantBCannotSeeTenantATopics()
    {
        var ct = TestContext.Current.CancellationToken;
        var storeA = CreateTopicStore(_providerA);
        var storeB = CreateTopicStore(_providerB);

        var topic = await storeA.AddAsync(AgentA, "tenant-a-only", null, ct);

        (await storeB.GetAsync(AgentA, topic.Id, ct)).ShouldBeNull();
        var listed = await storeB.ListAsync(AgentA, 10, 0, ct);
        listed.ShouldBeEmpty();
    }
}
