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
using Cvoya.Spring.Dapr.Threads;

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
        services.AddScoped<IThreadRegistry, EfThreadRegistry>();
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
        // Multi-Hat thread (both bound Hats are canonical participants);
        // hatA received the inbound, hatB did not. ADR-0062 § 5 says
        // reply as the Hat that received the inbound, so the resolver
        // must tie-break to hatA — even though hatB is the primary.
        var hatA = await SeedBoundHumanAsync();
        var hatB = await SeedBoundHumanAsync();
        var threadId = Guid.NewGuid();
        await SeedThreadWithInboundToAsync(threadId, hatA, additionalParticipants: hatB);
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
    public async Task PickFromAsync_OriginatedAsHat_ReturnsThreadParticipantHat()
    {
        var ct = TestContext.Current.CancellationToken;
        // #2865: regression for the "thread split" bug. The caller has
        // two bound Hats. The thread's canonical participant set
        // contains hatA but NOT hatB. PrimaryHumanId is hatB. The old
        // resolver fell through to PrimaryHumanId (hatB), which is not
        // a thread participant — and the message landed on a thread
        // whose canonical set excluded its sender, splitting the
        // conversation. The new resolver intersects bound Hats with
        // the canonical participant set and returns hatA — the only
        // bound Hat that is a thread participant. The thread has no
        // prior inbound (originated-as case): the resolver does NOT
        // need a received-as message to pin the right Hat.
        var hatA = await SeedBoundHumanAsync();
        var hatB = await SeedBoundHumanAsync();
        await SetPrimaryAsync(hatB);
        var threadId = Guid.NewGuid();
        await SeedThreadAsync(threadId, hatA);

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
    public async Task PickFromAsync_OriginatedAsHat_MultipleBoundParticipants_TieBreaksToLastSent()
    {
        var ct = TestContext.Current.CancellationToken;
        // Multi-Hat thread the caller originated (no received-as
        // inbound). Two bound Hats are both canonical participants;
        // hatA is the more recent sender on the thread. The resolver
        // tie-breaks to hatA (the originated-as fallback).
        var hatA = await SeedBoundHumanAsync();
        var hatB = await SeedBoundHumanAsync();
        var threadId = Guid.NewGuid();
        await SeedThreadAsync(threadId, hatA, hatB);
        await SeedOutboundFromAsync(threadId, hatB);
        await Task.Delay(5, ct); // ensure SentAt ordering is deterministic
        await SeedOutboundFromAsync(threadId, hatA);

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
    public async Task PickFromAsync_NoBoundHatInThreadParticipants_FallsBackToPrimary()
    {
        var ct = TestContext.Current.CancellationToken;
        // The thread's canonical participant set has no Hat the caller
        // is bound to. The resolver must NOT pick from the thread —
        // it falls through to PrimaryHumanId. The endpoint-level
        // SenderNotInThread gate (MessageEndpoints / ThreadEndpoints)
        // catches the resulting mismatch; the resolver does not
        // pre-empt that decision.
        var primary = await SeedBoundHumanAsync();
        await SetPrimaryAsync(primary);
        var threadId = Guid.NewGuid();
        var outsiderHat = Guid.NewGuid();
        await SeedThreadAsync(threadId, outsiderHat);

        var resolver = await CreateResolverAsync();

        var address = await resolver.PickFromAsync(
            CallerTenantUserId,
            explicitFromHumanId: null,
            threadId: threadId,
            ct);

        address.Scheme.ShouldBe(Address.HumanScheme);
        address.Id.ShouldBe(primary);
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
        var threadRegistry = scope.ServiceProvider.GetRequiredService<IThreadRegistry>();
        // Hold the scope alive by detaching the DbContext from disposal;
        // tests are single-threaded and the in-memory DB shares state by
        // name regardless.
        return await Task.FromResult(new TenantUserHumanResolver(db, threadRegistry));
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

    private async Task SeedThreadWithInboundToAsync(Guid threadId, Guid humanId, params Guid[] additionalParticipants)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        // #2865: the resolver now reads the thread's canonical participant
        // set, so the seed has to render the participants array the same
        // way EfThreadRegistry would (lowercased scheme + no-dash hex,
        // sorted, joined). Include the inbound recipient and any extra
        // bound Hats the test wants in the canonical set.
        var addresses = new List<string> { $"{Address.HumanScheme}:{humanId:N}" };
        foreach (var extra in additionalParticipants)
        {
            addresses.Add($"{Address.HumanScheme}:{extra:N}");
        }
        addresses.Sort(StringComparer.Ordinal);
        db.Threads.Add(new ThreadEntity
        {
            Id = threadId,
            TenantId = TenantId,
            ParticipantKey = string.Join('|', addresses),
            Participants = System.Text.Json.JsonSerializer.Serialize(addresses),
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

    private async Task SeedThreadAsync(Guid threadId, params Guid[] humanParticipants)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var addresses = humanParticipants
            .Select(h => $"{Address.HumanScheme}:{h:N}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        db.Threads.Add(new ThreadEntity
        {
            Id = threadId,
            TenantId = TenantId,
            ParticipantKey = string.Join('|', addresses),
            Participants = System.Text.Json.JsonSerializer.Serialize(addresses),
            ParticipantNameSnapshots = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedOutboundFromAsync(Guid threadId, Guid senderHumanId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            ThreadId = threadId,
            SenderScheme = Address.HumanScheme,
            SenderId = senderHumanId,
            RecipientScheme = Address.UnitScheme,
            RecipientId = Guid.NewGuid(),
            MessageType = MessageType.Domain.ToString(),
            Payload = "null",
            SentAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
