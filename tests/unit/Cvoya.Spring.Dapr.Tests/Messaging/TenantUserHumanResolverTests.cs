// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Messaging;

using System;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Messaging;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="TenantUserHumanResolver"/> — the API-boundary
/// rewrite from auth principal (<c>tenant-user://</c>) to routable
/// speaking-as Hat (<c>human://</c>) per ADR-0062 § 3. Pins the four
/// resolution branches called out in the ADR.
/// </summary>
public class TenantUserHumanResolverTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-2222-2222-2222-000000000099");
    private static readonly Guid CallerTenantUserId = OssTenantUserIds.Operator;

    private readonly ServiceProvider _provider;
    private readonly string _dbName;

    public TenantUserHumanResolverTests()
    {
        _dbName = $"resolver-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantId));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task PickFromAsync_ExplicitFrom_Bound_ReturnsThatHuman()
    {
        var ct = TestContext.Current.CancellationToken;
        var humanId = await SeedBoundHumanAsync();
        var resolver = await CreateResolverAsync();

        var address = await resolver.PickFromAsync(
            CallerTenantUserId,
            explicitFromHumanId: humanId,
            threadId: null,
            ct);

        address.Scheme.ShouldBe(Address.HumanScheme);
        address.Id.ShouldBe(humanId);
    }

    [Fact]
    public async Task PickFromAsync_ExplicitFrom_Unbound_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedBoundHumanAsync();
        // Plant a Human bound to a DIFFERENT TenantUser; the caller is
        // not allowed to use it as From.
        var otherTenantUser = Guid.Parse("ffffffff-0000-0000-0000-000000000099");
        var otherHumanId = await SeedHumanForTenantUserAsync(otherTenantUser);
        var resolver = await CreateResolverAsync();

        var ex = await Should.ThrowAsync<NoBoundHumanException>(() =>
            resolver.PickFromAsync(
                CallerTenantUserId,
                explicitFromHumanId: otherHumanId,
                threadId: null,
                ct));
        ex.Message.ShouldContain(otherHumanId.ToString());
    }

    [Fact]
    public async Task PickFromAsync_ThreadPinnedHat_ReturnsHatThatReceivedInbound()
    {
        var ct = TestContext.Current.CancellationToken;
        // Caller is bound to two Hats; one received the inbound on the
        // thread, the other did not. The resolver must pick the one that
        // received the inbound.
        var hatA = await SeedBoundHumanAsync();
        var hatB = await SeedBoundHumanAsync();
        var threadId = Guid.NewGuid();
        await SeedThreadWithInboundToAsync(threadId, hatA);
        // Also set a different primary so the test can prove
        // thread-pinned wins over primary.
        await SetPrimaryAsync(hatB);

        var resolver = await CreateResolverAsync();

        var address = await resolver.PickFromAsync(
            CallerTenantUserId,
            explicitFromHumanId: null,
            threadId: threadId,
            ct);

        address.Scheme.ShouldBe(Address.HumanScheme);
        address.Id.ShouldBe(hatA);
    }

    [Fact]
    public async Task PickFromAsync_NoThreadInbound_FallsBackToPrimaryHumanId()
    {
        var ct = TestContext.Current.CancellationToken;
        var primary = await SeedBoundHumanAsync();
        var other = await SeedBoundHumanAsync();
        await SetPrimaryAsync(primary);
        var resolver = await CreateResolverAsync();

        var address = await resolver.PickFromAsync(
            CallerTenantUserId,
            explicitFromHumanId: null,
            threadId: Guid.NewGuid(),  // unknown thread — no inbound match
            ct);

        address.Scheme.ShouldBe(Address.HumanScheme);
        address.Id.ShouldBe(primary);
        other.ShouldNotBe(primary);  // sanity
    }

    [Fact]
    public async Task PickFromAsync_NoPrimary_FallsBackToAnyBoundHuman()
    {
        var ct = TestContext.Current.CancellationToken;
        var human = await SeedBoundHumanAsync();
        var resolver = await CreateResolverAsync();

        var address = await resolver.PickFromAsync(
            CallerTenantUserId,
            explicitFromHumanId: null,
            threadId: null,
            ct);

        address.Scheme.ShouldBe(Address.HumanScheme);
        address.Id.ShouldBe(human);
    }

    [Fact]
    public async Task PickFromAsync_NoBoundHumans_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        // Seed only the TenantUser row, with no bound Humans at all.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.TenantUsers.Add(new TenantUserEntity
        {
            Id = CallerTenantUserId,
            TenantId = TenantId,
            DisplayName = "Operator",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        var resolver = await CreateResolverAsync();

        await Should.ThrowAsync<NoBoundHumanException>(() =>
            resolver.PickFromAsync(
                CallerTenantUserId,
                explicitFromHumanId: null,
                threadId: null,
                ct));
    }

    [Fact]
    public async Task PickFromAsync_CallerIsEmpty_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = await CreateResolverAsync();

        await Should.ThrowAsync<ArgumentException>(() =>
            resolver.PickFromAsync(
                Guid.Empty,
                explicitFromHumanId: null,
                threadId: null,
                ct));
    }

    private async Task<TenantUserHumanResolver> CreateResolverAsync()
    {
        var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        // Hold the scope alive by detaching the DbContext from disposal;
        // tests are single-threaded and the in-memory DB shares state by
        // name regardless.
        return await Task.FromResult(new TenantUserHumanResolver(db));
    }

    private async Task<Guid> SeedBoundHumanAsync()
    {
        await EnsureTenantUserAsync(CallerTenantUserId);
        var humanId = Guid.NewGuid();
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.Humans.Add(new HumanEntity
        {
            Id = humanId,
            TenantId = TenantId,
            TenantUserId = CallerTenantUserId,
            Username = $"u-{humanId:N}",
            DisplayName = $"u-{humanId:N}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return humanId;
    }

    private async Task<Guid> SeedHumanForTenantUserAsync(Guid tenantUserId)
    {
        await EnsureTenantUserAsync(tenantUserId);
        var humanId = Guid.NewGuid();
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.Humans.Add(new HumanEntity
        {
            Id = humanId,
            TenantId = TenantId,
            TenantUserId = tenantUserId,
            Username = $"o-{humanId:N}",
            DisplayName = $"o-{humanId:N}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return humanId;
    }

    private async Task EnsureTenantUserAsync(Guid tenantUserId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var exists = await db.TenantUsers.AnyAsync(
            u => u.Id == tenantUserId,
            TestContext.Current.CancellationToken);
        if (!exists)
        {
            db.TenantUsers.Add(new TenantUserEntity
            {
                Id = tenantUserId,
                TenantId = TenantId,
                DisplayName = $"u-{tenantUserId:N}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    private async Task SetPrimaryAsync(Guid humanId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var tu = await db.TenantUsers.SingleAsync(
            u => u.Id == CallerTenantUserId,
            TestContext.Current.CancellationToken);
        tu.PrimaryHumanId = humanId;
        tu.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedThreadWithInboundToAsync(Guid threadId, Guid humanId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.Threads.Add(new ThreadEntity
        {
            Id = threadId,
            TenantId = TenantId,
            ParticipantKey = $"k-{threadId:N}",
            Participants = "[]",
            ParticipantNameSnapshots = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        });
        db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            ThreadId = threadId,
            SenderScheme = Address.AgentScheme,
            SenderId = Guid.NewGuid(),
            RecipientScheme = Address.HumanScheme,
            RecipientId = humanId,
            MessageType = MessageType.Domain.ToString(),
            Payload = "null",
            SentAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
