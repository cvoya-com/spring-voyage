// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ThreadQueryService"/> — the EF-authoritative
/// projection over the <c>threads</c> + <c>messages</c> tables landed in
/// #2047 and #2053. Pre-rewrite the projection scanned <c>activity_events</c>
/// JSON to materialise threads (#2054 retires that path); these tests seed
/// the relational rows directly and assert the read shape downstream
/// surfaces depend on.
/// </summary>
public class ThreadQueryServiceTests : IDisposable
{
    private static readonly Guid TenantId = new("aaaa1001-2222-3333-4444-000000000001");
    private static readonly Guid OtherTenantId = new("aaaa1001-2222-3333-4444-000000000002");

    private readonly DbContextOptions<SpringDbContext> _dbOptions;
    private readonly SpringDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IParticipantDisplayNameResolver _participantResolver = NotDeletedResolver();

    /// <summary>
    /// Tracks the seed-time scheme + display value for every actor id.
    /// Retained for the <c>NewActor</c> helper to build addresses with a
    /// readable label; post-#2082 the projection no longer consults a
    /// directory for filter resolution.
    /// </summary>
    private readonly Dictionary<Guid, (string Scheme, string Slug)> _seededActors = new();

    public ThreadQueryServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase($"ThreadQueryTest-{Guid.NewGuid()}")
            .Options;
        _tenantContext = new StaticTenantContext(TenantId);
        _db = new SpringDbContext(_dbOptions, _tenantContext);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private ThreadQueryService BuildService() => new(_db, _participantResolver);

    /// <summary>
    /// Default resolver substitute: every address is reported as not
    /// deleted, so threads default to <c>IsArchived = false</c>. The
    /// IsArchived-specific tests below override per-address via
    /// <c>_participantResolver.IsDeletedAsync(...).Returns(...)</c>.
    /// </summary>
    private static IParticipantDisplayNameResolver NotDeletedResolver()
    {
        var stub = Substitute.For<IParticipantDisplayNameResolver>();
        stub.IsDeletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(false));
        return stub;
    }

    [Fact]
    public async Task ListAsync_NoThreads_ReturnsEmpty()
    {
        var svc = BuildService();

        var result = await svc.ListAsync(new ThreadQueryFilters(), TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_ListsThreadsWithParticipantsCountsAndOrigin()
    {
        var ct = TestContext.Current.CancellationToken;

        var ada = NewActor("agent", "ada");
        var grace = NewActor("agent", "grace");
        var savasp = NewActor("human", "savasp");

        var (thread1, _) = await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-10),
            new[]
            {
                NewMessage(ada, savasp, "Hello, savasp.", DateTimeOffset.UtcNow.AddMinutes(-9)),
                NewMessage(savasp, ada, "Hi ada", DateTimeOffset.UtcNow.AddMinutes(-8)),
            });

        var (thread2, _) = await SeedThreadAsync(
            new[] { grace },
            DateTimeOffset.UtcNow.AddMinutes(-20),
            new[] { NewMessage(grace, savasp, "Lone send", DateTimeOffset.UtcNow.AddMinutes(-19)) });

        var svc = BuildService();
        var result = await svc.ListAsync(new ThreadQueryFilters(), ct);

        result.Count.ShouldBe(2);

        var t1 = result.Single(s => s.Id == GuidFormatter.Format(thread1));
        t1.Participants.ShouldContain($"agent:{ada.Id:N}");
        t1.Participants.ShouldContain($"human:{savasp.Id:N}");
        t1.EventCount.ShouldBe(2);
        t1.Origin.ShouldBe($"agent:{ada.Id:N}");
        t1.Summary.ShouldBe("Hello, savasp.");

        var t2 = result.Single(s => s.Id == GuidFormatter.Format(thread2));
        t2.EventCount.ShouldBe(1);
    }

    [Fact]
    public async Task ListAsync_AgentFilter_MatchesByActorIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, savasp, "first", DateTimeOffset.UtcNow.AddMinutes(-4)) });

        var svc = BuildService();
        var result = await svc.ListAsync(
            new ThreadQueryFilters(Agent: ada.Id.ToString()),
            ct);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(GuidFormatter.Format(threadId));
    }

    [Fact]
    public async Task ListAsync_SinceFilter_ExcludesOlderThreads()
    {
        // #2790: Since narrows the result set to threads whose last
        // activity is at or after the given instant. Anchors a fixed
        // "now" so the assertions stay deterministic — the bucketing
        // logic in the portal uses the same comparison.
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var now = DateTimeOffset.UtcNow;

        var (recentId, _) = await SeedThreadAsync(
            new[] { ada, savasp },
            now.AddHours(-1),
            new[] { NewMessage(ada, savasp, "fresh", now.AddHours(-1)) },
            participantKeyOverride: "since-recent");
        await SeedThreadAsync(
            new[] { ada, savasp },
            now.AddDays(-30),
            new[] { NewMessage(ada, savasp, "ancient", now.AddDays(-30)) },
            participantKeyOverride: "since-old");

        var svc = BuildService();
        var result = await svc.ListAsync(
            new ThreadQueryFilters(Since: now.AddDays(-1)),
            ct);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(GuidFormatter.Format(recentId));
    }

    [Fact]
    public async Task ListAsync_LimitCapsResultCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");

        for (var i = 0; i < 5; i++)
        {
            await SeedThreadAsync(
                new[] { ada, savasp },
                DateTimeOffset.UtcNow.AddMinutes(-i * 10),
                new[] { NewMessage(ada, savasp, $"thread {i}", DateTimeOffset.UtcNow.AddMinutes(-i * 10)) },
                participantKeyOverride: $"k-{i}");
        }

        var svc = BuildService();
        var result = await svc.ListAsync(new ThreadQueryFilters(Limit: 2), ct);
        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ListAsync_OtherTenant_DoesNotLeak()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, savasp, "secret", DateTimeOffset.UtcNow.AddMinutes(-5)) });

        // Re-bind to a different tenant; the DbContext query filter should
        // hide every row.
        using var otherDb = new SpringDbContext(_dbOptions, new StaticTenantContext(OtherTenantId));
        var svc = new ThreadQueryService(otherDb, _participantResolver);
        var result = await svc.ListAsync(new ThreadQueryFilters(), ct);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAsync_OrderedMessages_ReturnedOldestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-10);
        var later = DateTimeOffset.UtcNow.AddMinutes(-1);

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, savasp },
            earlier,
            new[]
            {
                NewMessage(ada, savasp, "later message", later),
                NewMessage(savasp, ada, "earlier message", earlier),
            });

        var svc = BuildService();
        var detail = await svc.GetAsync(GuidFormatter.Format(threadId), ct);

        detail.ShouldNotBeNull();
        detail!.Events.Count.ShouldBe(2);
        detail.Events[0].Body.ShouldBe("earlier message");
        detail.Events[1].Body.ShouldBe("later message");
        detail.Summary.Id.ShouldBe(GuidFormatter.Format(threadId));
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = BuildService();
        var detail = await svc.GetAsync(Guid.NewGuid().ToString("N"), ct);
        detail.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_NonGuidId_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = BuildService();
        var detail = await svc.GetAsync("not-a-guid", ct);
        detail.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_MessageBody_SurfacesEnvelopeAndBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");

        var (threadId, msgIds) = await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-1),
            new[] { NewMessage(savasp, ada, "Approve merge?", DateTimeOffset.UtcNow) });

        var svc = BuildService();
        var detail = await svc.GetAsync(GuidFormatter.Format(threadId), ct);

        detail.ShouldNotBeNull();
        var evt = detail!.Events.Single();
        evt.MessageId.ShouldBe(msgIds[0]);
        evt.From.ShouldBe($"human:{savasp.Id:N}");
        evt.To.ShouldBe($"agent:{ada.Id:N}");
        evt.Body.ShouldBe("Approve merge?");
    }

    [Fact]
    public async Task GetAsync_RetractedMessage_StillVisibleOnTimeline()
    {
        // Retraction is rendered as strikethrough on the surface, not as a
        // hidden row — the read service stays neutral.
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-2),
            new[] { NewMessage(ada, savasp, "wrong reply", DateTimeOffset.UtcNow.AddMinutes(-1)) },
            retract: true);

        var svc = BuildService();
        var detail = await svc.GetAsync(GuidFormatter.Format(threadId), ct);

        detail.ShouldNotBeNull();
        detail!.Events.Count.ShouldBe(1);
        detail.Events[0].Body.ShouldBe("wrong reply");
    }

    // -------------------------------------------------------------------
    // Inbox tests
    // -------------------------------------------------------------------

    [Fact]
    public async Task ListInboxAsync_HumanAwaitingAsk_AppearsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");

        await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-10),
            new[]
            {
                NewMessage(ada, savasp, "Approve merge?", DateTimeOffset.UtcNow.AddMinutes(-1)),
            });

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync(new[] { savasp.Id }, null, ct);

        inbox.Count.ShouldBe(1);
        inbox[0].Human.ShouldBe($"human:{savasp.Id:N}");
        inbox[0].From.ShouldBe($"agent:{ada.Id:N}");
        inbox[0].Summary.ShouldBe("Approve merge?");
    }

    [Fact]
    public async Task ListInboxAsync_HumanAlreadyReplied_DropsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");

        await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-10),
            new[]
            {
                NewMessage(ada, savasp, "Need input", DateTimeOffset.UtcNow.AddMinutes(-5)),
                NewMessage(savasp, ada, "On it", DateTimeOffset.UtcNow.AddMinutes(-1)),
            });

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync(new[] { savasp.Id }, null, ct);

        inbox.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListInboxAsync_DifferentHuman_DoesNotLeak()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var alice = NewActor("human", "alice");
        var savasp = NewActor("human", "savasp");

        await SeedThreadAsync(
            new[] { ada, alice },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, alice, "For alice", DateTimeOffset.UtcNow.AddMinutes(-1)) });

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync(new[] { savasp.Id }, null, ct);

        inbox.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListInboxAsync_FreshAgentReply_AppearsInInbox()
    {
        // Reproduces #1210 in the new model: an agent reply is visible
        // immediately after dispatch, even before the human reads / replies.
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2);

        await SeedThreadAsync(
            new[] { ada, savasp },
            t0,
            new[]
            {
                NewMessage(savasp, ada, "Hello, agent.", t0),
                NewMessage(ada, savasp, "On it.", t0.AddMinutes(1)),
            });

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync(new[] { savasp.Id }, null, ct);

        inbox.Count.ShouldBe(1);
        inbox[0].From.ShouldBe($"agent:{ada.Id:N}");
        inbox[0].Summary.ShouldBe("On it.");
    }

    [Fact]
    public async Task ListInboxAsync_OrdersByPendingSinceMostRecentFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var grace = NewActor("agent", "grace");
        var savasp = NewActor("human", "savasp");
        var stale = DateTimeOffset.UtcNow.AddMinutes(-90);
        var fresh = DateTimeOffset.UtcNow.AddMinutes(-1);

        var (staleId, _) = await SeedThreadAsync(
            new[] { grace, savasp },
            stale.AddMinutes(-1),
            new[] { NewMessage(grace, savasp, "Stale ask", stale) },
            participantKeyOverride: "stale");

        var (freshId, _) = await SeedThreadAsync(
            new[] { ada, savasp },
            fresh.AddSeconds(-90),
            new[] { NewMessage(ada, savasp, "Fresh reply", fresh) },
            participantKeyOverride: "fresh");

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync(new[] { savasp.Id }, null, ct);

        inbox.Count.ShouldBe(2);
        inbox[0].ThreadId.ShouldBe(GuidFormatter.Format(freshId));
        inbox[1].ThreadId.ShouldBe(GuidFormatter.Format(staleId));
    }

    [Fact]
    public async Task ListInboxAsync_NullLastReadAt_UnreadCountEqualsAllMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);

        await SeedThreadAsync(
            new[] { ada, savasp },
            t0,
            new[]
            {
                NewMessage(savasp, ada, "Q", t0),
                NewMessage(ada, savasp, "A1", t0.AddSeconds(30)),
                NewMessage(ada, savasp, "A2", t0.AddSeconds(60)),
            });

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync(new[] { savasp.Id }, null, ct);

        inbox.Count.ShouldBe(1);
        inbox[0].UnreadCount.ShouldBe(3);
    }

    [Fact]
    public async Task ListInboxAsync_WithLastReadAt_UnreadCountReflectsMessagesSince()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, savasp },
            t0,
            new[]
            {
                NewMessage(savasp, ada, "Q", t0),
                NewMessage(ada, savasp, "A1", t0.AddSeconds(30)),
                NewMessage(ada, savasp, "A2", t0.AddSeconds(60)),
            });

        // Read cursor is between message 2 and message 3 — only message 3 counts.
        var lastReadAt = new Dictionary<string, DateTimeOffset>
        {
            [GuidFormatter.Format(threadId)] = t0.AddSeconds(45),
        };

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync(new[] { savasp.Id }, lastReadAt, ct);

        inbox.Count.ShouldBe(1);
        inbox[0].UnreadCount.ShouldBe(1);
    }

    [Fact]
    public async Task ListInboxAsync_FullyReadThread_UnreadCountIsZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, savasp },
            t0,
            new[]
            {
                NewMessage(savasp, ada, "Q", t0),
                NewMessage(ada, savasp, "A", t0.AddSeconds(30)),
            });

        var lastReadAt = new Dictionary<string, DateTimeOffset>
        {
            [GuidFormatter.Format(threadId)] = t0.AddMinutes(10),
        };

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync(new[] { savasp.Id }, lastReadAt, ct);

        inbox.Count.ShouldBe(1);
        inbox[0].UnreadCount.ShouldBe(0);
    }

    [Fact]
    public async Task ListInboxAsync_OtherTenant_DoesNotLeak()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-2),
            new[] { NewMessage(ada, savasp, "secret", DateTimeOffset.UtcNow.AddMinutes(-1)) });

        using var otherDb = new SpringDbContext(_dbOptions, new StaticTenantContext(OtherTenantId));
        var svc = new ThreadQueryService(otherDb, _participantResolver);
        var inbox = await svc.ListInboxAsync(new[] { savasp.Id }, null, ct);

        inbox.ShouldBeEmpty();
    }

    // -------------------------------------------------------------------
    // IsArchived (#2732) — orphan-engagement derivation.
    // -------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_LiveAgentParticipant_IsArchivedFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, savasp, "Hello", DateTimeOffset.UtcNow.AddMinutes(-4)) });

        // Default substitute already reports every address as not-deleted.

        var svc = BuildService();
        // Bypass the default archived=false filter by explicitly requesting
        // the unfiltered shape (both live + archived).
        var result = await svc.ListAsync(new ThreadQueryFilters(), ct);

        var summary = result.Single(s => s.Id == GuidFormatter.Format(threadId));
        summary.IsArchived.ShouldBeFalse();
    }

    [Fact]
    public async Task ListAsync_DeletedAgentParticipant_IsArchivedTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var adaAddress = $"agent:{ada.Id:N}";

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, savasp, "Hello", DateTimeOffset.UtcNow.AddMinutes(-4)) });

        _participantResolver.IsDeletedAsync(adaAddress, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var svc = BuildService();
        // Default filter excludes archived; request archived-only to see this thread.
        var result = await svc.ListAsync(new ThreadQueryFilters(Archived: true), ct);

        var summary = result.Single(s => s.Id == GuidFormatter.Format(threadId));
        summary.IsArchived.ShouldBeTrue();
    }

    [Fact]
    public async Task ListAsync_OneDeletedOneLiveNonHuman_IsArchivedFalse()
    {
        // Mixed participant set: one live, one deleted. The orphan rule
        // requires EVERY non-human to be deleted, so this stays live.
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var grace = NewActor("agent", "grace");
        var savasp = NewActor("human", "savasp");
        var adaAddress = $"agent:{ada.Id:N}";

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, grace, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, savasp, "Hello", DateTimeOffset.UtcNow.AddMinutes(-4)) });

        // ada deleted, grace live, savasp human.
        _participantResolver.IsDeletedAsync(adaAddress, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var svc = BuildService();
        var result = await svc.ListAsync(new ThreadQueryFilters(), ct);

        var summary = result.Single(s => s.Id == GuidFormatter.Format(threadId));
        summary.IsArchived.ShouldBeFalse();
    }

    [Fact]
    public async Task ListAsync_SoloHumanThread_IsArchivedFalse()
    {
        // No non-human participants → orphan rule does not apply (the
        // "every non-human is deleted" predicate is vacuously true on an
        // empty set, but the orphan UX requires at least one non-human
        // on the thread for archiving to be meaningful).
        var ct = TestContext.Current.CancellationToken;
        var savasp = NewActor("human", "savasp");

        var (threadId, _) = await SeedThreadAsync(
            new[] { savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(savasp, savasp, "Solo note", DateTimeOffset.UtcNow.AddMinutes(-4)) });

        var svc = BuildService();
        var result = await svc.ListAsync(new ThreadQueryFilters(), ct);

        var summary = result.Single(s => s.Id == GuidFormatter.Format(threadId));
        summary.IsArchived.ShouldBeFalse();
    }

    [Fact]
    public async Task ListAsync_DeletedConnectorLiveUnit_IsArchivedFalse()
    {
        // Cross-scheme mix: a deleted connector + a live unit on the
        // same thread. Live unit keeps the thread out of the archive.
        var ct = TestContext.Current.CancellationToken;
        var connector = NewActor("connector", "github");
        var eng = NewActor("unit", "eng");
        var savasp = NewActor("human", "savasp");
        var connectorAddress = $"connector:{connector.Id:N}";

        var (threadId, _) = await SeedThreadAsync(
            new[] { connector, eng, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(connector, eng, "pr opened", DateTimeOffset.UtcNow.AddMinutes(-4)) });

        _participantResolver.IsDeletedAsync(connectorAddress, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var svc = BuildService();
        var result = await svc.ListAsync(new ThreadQueryFilters(), ct);

        var summary = result.Single(s => s.Id == GuidFormatter.Format(threadId));
        summary.IsArchived.ShouldBeFalse();
    }

    [Fact]
    public async Task ListAsync_DefaultFilter_ExcludesArchivedThreads()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var grace = NewActor("agent", "grace");
        var savasp = NewActor("human", "savasp");
        var adaAddress = $"agent:{ada.Id:N}";

        // Thread 1: live (ada is live).
        await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, savasp, "live", DateTimeOffset.UtcNow.AddMinutes(-4)) },
            participantKeyOverride: "live-thread");

        // Thread 2: archived (grace is the only non-human, deleted).
        var (archivedId, _) = await SeedThreadAsync(
            new[] { grace, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-10),
            new[] { NewMessage(grace, savasp, "orphan", DateTimeOffset.UtcNow.AddMinutes(-9)) },
            participantKeyOverride: "archived-thread");

        var graceAddress = $"agent:{grace.Id:N}";
        _participantResolver.IsDeletedAsync(graceAddress, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));
        _participantResolver.IsDeletedAsync(adaAddress, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(false));

        var svc = BuildService();
        var result = await svc.ListAsync(new ThreadQueryFilters(), ct);

        // Only the live thread survives the default filter.
        result.Count.ShouldBe(1);
        result[0].IsArchived.ShouldBeFalse();
        result[0].Id.ShouldNotBe(GuidFormatter.Format(archivedId));
    }

    [Fact]
    public async Task ListAsync_ArchivedFalse_ExcludesArchivedThreads()
    {
        // Explicit archived=false behaves identically to the omitted
        // case — defensive: the explicit-null branch is covered by
        // ListAsync_DefaultFilter_ExcludesArchivedThreads above.
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var adaAddress = $"agent:{ada.Id:N}";

        await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, savasp, "orphan", DateTimeOffset.UtcNow.AddMinutes(-4)) });

        _participantResolver.IsDeletedAsync(adaAddress, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var svc = BuildService();
        var result = await svc.ListAsync(new ThreadQueryFilters(Archived: false), ct);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_ArchivedTrue_ReturnsOnlyArchivedThreads()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var grace = NewActor("agent", "grace");
        var savasp = NewActor("human", "savasp");

        // Live thread.
        await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, savasp, "live", DateTimeOffset.UtcNow.AddMinutes(-4)) },
            participantKeyOverride: "live-thread");

        // Archived thread.
        var (archivedId, _) = await SeedThreadAsync(
            new[] { grace, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-10),
            new[] { NewMessage(grace, savasp, "orphan", DateTimeOffset.UtcNow.AddMinutes(-9)) },
            participantKeyOverride: "archived-thread");

        _participantResolver.IsDeletedAsync($"agent:{grace.Id:N}", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var svc = BuildService();
        var result = await svc.ListAsync(new ThreadQueryFilters(Archived: true), ct);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(GuidFormatter.Format(archivedId));
        result[0].IsArchived.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAsync_DeletedAgentParticipant_ReturnsIsArchivedTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var adaAddress = $"agent:{ada.Id:N}";

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, savasp, "Hello", DateTimeOffset.UtcNow.AddMinutes(-4)) });

        _participantResolver.IsDeletedAsync(adaAddress, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var svc = BuildService();
        var detail = await svc.GetAsync(GuidFormatter.Format(threadId), ct);

        detail.ShouldNotBeNull();
        detail!.Summary.IsArchived.ShouldBeTrue();
    }

    // -------------------------------------------------------------------
    // ToParseable
    // -------------------------------------------------------------------
    // RecipientHumanId — ADR-0062 § 5 / #2826 read-side Hat resolution
    // -------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_RecipientHumanId_TakesLatestHumanRecipient()
    {
        // The recipient Hat is the Human id on the most recent message
        // addressed to a human recipient. Seed a multi-message thread
        // where the latest human-addressed message picks `savasp`; the
        // earlier human-addressed message picks a different (also
        // human) recipient that must NOT win.
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var other = NewActor("human", "other");
        var now = DateTimeOffset.UtcNow;

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, savasp, other },
            now.AddMinutes(-30),
            new[]
            {
                NewMessage(ada, other, "older", now.AddMinutes(-20)),
                NewMessage(ada, savasp, "newer", now.AddMinutes(-5)),
                // A subsequent A2A message must NOT clear the human pick —
                // RecipientHumanId is "latest message addressed to a
                // human", not "latest message overall".
                NewMessage(ada, ada, "self-a2a", now.AddMinutes(-1)),
            });

        var svc = BuildService();
        var result = await svc.ListAsync(new ThreadQueryFilters(), ct);

        var summary = result.Single(s => s.Id == GuidFormatter.Format(threadId));
        summary.RecipientHumanId.ShouldBe(savasp.Id);
    }

    [Fact]
    public async Task ListAsync_RecipientHumanId_NullForPureA2aThread()
    {
        // Pure A2A thread: no message is addressed to a human:
        // recipient. RecipientHumanId must surface as null so the
        // portal hides the chip.
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var grace = NewActor("agent", "grace");
        var now = DateTimeOffset.UtcNow;

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, grace },
            now.AddMinutes(-10),
            new[]
            {
                NewMessage(ada, grace, "hi", now.AddMinutes(-9)),
                NewMessage(grace, ada, "hi back", now.AddMinutes(-8)),
            });

        var svc = BuildService();
        var result = await svc.ListAsync(new ThreadQueryFilters(), ct);

        var summary = result.Single(s => s.Id == GuidFormatter.Format(threadId));
        summary.RecipientHumanId.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_RecipientHumanId_TakesLatestHumanRecipient()
    {
        // The per-thread `GetAsync` walks the already-fetched in-memory
        // message list rather than re-querying. Same contract as the
        // list path — most recent message addressed to a human wins.
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var other = NewActor("human", "other");
        var now = DateTimeOffset.UtcNow;

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, savasp, other },
            now.AddMinutes(-30),
            new[]
            {
                NewMessage(ada, other, "older", now.AddMinutes(-20)),
                NewMessage(ada, savasp, "newer", now.AddMinutes(-5)),
            });

        var svc = BuildService();
        var detail = await svc.GetAsync(GuidFormatter.Format(threadId), ct);

        detail.ShouldNotBeNull();
        detail!.Summary.RecipientHumanId.ShouldBe(savasp.Id);
    }

    [Fact]
    public async Task GetAsync_RecipientHumanId_NullForPureA2aThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var grace = NewActor("agent", "grace");
        var now = DateTimeOffset.UtcNow;

        var (threadId, _) = await SeedThreadAsync(
            new[] { ada, grace },
            now.AddMinutes(-10),
            new[] { NewMessage(ada, grace, "hi", now.AddMinutes(-9)) });

        var svc = BuildService();
        var detail = await svc.GetAsync(GuidFormatter.Format(threadId), ct);

        detail.ShouldNotBeNull();
        detail!.Summary.RecipientHumanId.ShouldBeNull();
    }

    // -------------------------------------------------------------------

    [Fact]
    public void ToParseable_IdentityForm_ReducesToCanonicalForm()
    {
        var hex = "2ab56e09674640b29a34f0d6babfc0f3";
        ThreadQueryService.ToParseable($"agent:id:{hex}").ShouldBe($"agent:{hex}");
    }

    [Fact]
    public void ToParseable_NavigationForm_ReducesToCanonicalForm()
    {
        var hex = "2ab56e09674640b29a34f0d6babfc0f3";
        ThreadQueryService.ToParseable($"human://{hex}").ShouldBe($"human:{hex}");
    }

    [Fact]
    public void ToParseable_AlreadyCanonical_Passthrough()
    {
        var hex = "2ab56e09674640b29a34f0d6babfc0f3";
        ThreadQueryService.ToParseable($"agent:{hex}").ShouldBe($"agent:{hex}");
    }

    // -------------------------------------------------------------------
    // GetMessagesByIdsAsync — #2990 by-id, permission-checked lookup
    // -------------------------------------------------------------------

    [Fact]
    public async Task GetMessagesByIds_OwnThread_ReturnsMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");

        var (_, msgIds) = await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(savasp, ada, "Approve merge?", DateTimeOffset.UtcNow.AddMinutes(-4)) });

        var svc = BuildService();
        var result = await svc.GetMessagesByIdsAsync(
            ada.ToString(),
            new[] { GuidFormatter.Format(msgIds[0]) },
            ct);

        result.Skipped.ShouldBeEmpty();
        result.Messages.Count.ShouldBe(1);
        result.Messages[0].MessageId.ShouldBe(msgIds[0]);
        result.Messages[0].From.ShouldBe($"human:{savasp.Id:N}");
        result.Messages[0].To.ShouldBe($"agent:{ada.Id:N}");
        result.Messages[0].Body.ShouldBe("Approve merge?");
    }

    [Fact]
    public async Task GetMessagesByIds_ForeignThread_Skipped()
    {
        // The message exists, but the caller is not on its thread — it must
        // be skipped, not returned, and indistinguishable from not-found.
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var outsider = NewActor("agent", "outsider");

        var (_, msgIds) = await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, savasp, "private", DateTimeOffset.UtcNow.AddMinutes(-4)) });

        var svc = BuildService();
        var result = await svc.GetMessagesByIdsAsync(
            outsider.ToString(),
            new[] { GuidFormatter.Format(msgIds[0]) },
            ct);

        result.Messages.ShouldBeEmpty();
        result.Skipped.ShouldBe(new[] { GuidFormatter.Format(msgIds[0]) });
    }

    [Fact]
    public async Task GetMessagesByIds_UnknownId_Skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var unknown = GuidFormatter.Format(Guid.NewGuid());

        var svc = BuildService();
        var result = await svc.GetMessagesByIdsAsync(ada.ToString(), new[] { unknown }, ct);

        result.Messages.ShouldBeEmpty();
        result.Skipped.ShouldBe(new[] { unknown });
    }

    [Fact]
    public async Task GetMessagesByIds_MalformedId_Skipped()
    {
        // A syntactically-invalid id can never resolve — it joins the
        // collapsed skipped bucket rather than erroring the batch.
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");

        var svc = BuildService();
        var result = await svc.GetMessagesByIdsAsync(ada.ToString(), new[] { "not-a-guid" }, ct);

        result.Messages.ShouldBeEmpty();
        result.Skipped.ShouldBe(new[] { "not-a-guid" });
    }

    [Fact]
    public async Task GetMessagesByIds_MixedBatch_PartitionsAndPreservesInputOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");
        var now = DateTimeOffset.UtcNow;

        var (_, ownIds) = await SeedThreadAsync(
            new[] { ada, savasp },
            now.AddMinutes(-10),
            new[]
            {
                NewMessage(ada, savasp, "first", now.AddMinutes(-9)),
                NewMessage(savasp, ada, "second", now.AddMinutes(-8)),
            },
            participantKeyOverride: "own");

        // A message on a thread `ada` is not on.
        var grace = NewActor("agent", "grace");
        var alice = NewActor("human", "alice");
        var (_, foreignIds) = await SeedThreadAsync(
            new[] { grace, alice },
            now.AddMinutes(-7),
            new[] { NewMessage(grace, alice, "not for ada", now.AddMinutes(-6)) },
            participantKeyOverride: "foreign");

        var unknown = GuidFormatter.Format(Guid.NewGuid());
        var secondOwn = GuidFormatter.Format(ownIds[1]);
        var firstOwn = GuidFormatter.Format(ownIds[0]);
        var foreign = GuidFormatter.Format(foreignIds[0]);

        var svc = BuildService();
        // Request order intentionally differs from chronological order so the
        // assertion proves input order is preserved, not sent-at order.
        var result = await svc.GetMessagesByIdsAsync(
            ada.ToString(),
            new[] { secondOwn, unknown, firstOwn, foreign },
            ct);

        result.Messages.Select(m => GuidFormatter.Format(m.MessageId))
            .ShouldBe(new[] { secondOwn, firstOwn });
        result.Skipped.ShouldBe(new[] { unknown, foreign });
    }

    [Fact]
    public async Task GetMessagesByIds_DuplicateIds_ReturnedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");

        var (_, msgIds) = await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, savasp, "once", DateTimeOffset.UtcNow.AddMinutes(-4)) });

        var id = GuidFormatter.Format(msgIds[0]);

        var svc = BuildService();
        var result = await svc.GetMessagesByIdsAsync(ada.ToString(), new[] { id, id }, ct);

        result.Messages.Count.ShouldBe(1);
        result.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetMessagesByIds_EmptyInput_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");

        var svc = BuildService();
        var result = await svc.GetMessagesByIdsAsync(ada.ToString(), Array.Empty<string>(), ct);

        result.Messages.ShouldBeEmpty();
        result.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetMessagesByIds_OtherTenant_DoesNotLeak()
    {
        // A message seeded in this tenant must be invisible (skipped, as
        // not-found) to a caller bound to a different tenant.
        var ct = TestContext.Current.CancellationToken;
        var ada = NewActor("agent", "ada");
        var savasp = NewActor("human", "savasp");

        var (_, msgIds) = await SeedThreadAsync(
            new[] { ada, savasp },
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new[] { NewMessage(ada, savasp, "secret", DateTimeOffset.UtcNow.AddMinutes(-4)) });

        using var otherDb = new SpringDbContext(_dbOptions, new StaticTenantContext(OtherTenantId));
        var svc = new ThreadQueryService(otherDb, _participantResolver);
        var result = await svc.GetMessagesByIdsAsync(
            ada.ToString(),
            new[] { GuidFormatter.Format(msgIds[0]) },
            ct);

        result.Messages.ShouldBeEmpty();
        result.Skipped.ShouldBe(new[] { GuidFormatter.Format(msgIds[0]) });
    }

    // -------------------------------------------------------------------
    // Seed helpers
    // -------------------------------------------------------------------

    private Address NewActor(string scheme, string slug)
    {
        var id = Guid.NewGuid();
        _seededActors[id] = (scheme, slug);
        return new Address(scheme, id);
    }

    private static (Address From, Address To, string? Body, DateTimeOffset SentAt) NewMessage(
        Address from,
        Address to,
        string? body,
        DateTimeOffset sentAt) => (from, to, body, sentAt);

    private async Task<(Guid ThreadId, IReadOnlyList<Guid> MessageIds)> SeedThreadAsync(
        Address[] participants,
        DateTimeOffset createdAt,
        (Address From, Address To, string? Body, DateTimeOffset SentAt)[] messages,
        string? participantKeyOverride = null,
        bool retract = false)
    {
        var threadId = Guid.NewGuid();
        var canonical = participants
            .Select(a => $"{a.Scheme}:{GuidFormatter.Format(a.Id)}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var thread = new ThreadEntity
        {
            Id = threadId,
            TenantId = TenantId,
            ParticipantKey = participantKeyOverride
                ?? string.Join('|', canonical),
            Participants = JsonSerializer.Serialize(canonical),
            CreatedAt = createdAt,
            LastActivityAt = messages.Length > 0
                ? messages.Max(m => m.SentAt)
                : createdAt,
        };
        _db.Threads.Add(thread);

        var messageIds = new List<Guid>();
        foreach (var m in messages)
        {
            var id = Guid.NewGuid();
            messageIds.Add(id);
            _db.Messages.Add(new MessageEntity
            {
                Id = id,
                TenantId = TenantId,
                ThreadId = threadId,
                SenderScheme = m.From.Scheme,
                SenderId = m.From.Id,
                RecipientScheme = m.To.Scheme,
                RecipientId = m.To.Id,
                MessageType = nameof(MessageType.Domain),
                Body = m.Body,
                Payload = m.Body is null ? "null" : JsonSerializer.Serialize(m.Body),
                SentAt = m.SentAt,
                RetractedAt = retract ? m.SentAt.AddSeconds(5) : null,
            });
        }

        await _db.SaveChangesAsync();
        return (threadId, messageIds);
    }
}
