// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Observability;

using Microsoft.EntityFrameworkCore;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ThreadQueryService"/> — the projection that
/// turns activity events into conversation summaries / detail / inbox rows
/// for #452 and #456. Tests use an in-memory EF context so they exercise the
/// real LINQ grouping without standing up Postgres.
/// </summary>
public class ThreadQueryServiceTests : IDisposable
{
    private readonly SpringDbContext _db;

    public ThreadQueryServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase($"ThreadQueryTest-{Guid.NewGuid()}")
            .Options;
        _db = new SpringDbContext(dbOptions);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ListAsync_NoActivity_ReturnsEmpty()
    {
        var svc = new ThreadQueryService(_db);

        var result = await svc.ListAsync(new ThreadQueryFilters(), TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_GroupsEventsByCorrelationIdAndDerivesParticipants()
    {
        await SeedThreadAsync("c-1", new[]
        {
            ("agent:ada", "ThreadStarted", "Started c-1", DateTimeOffset.UtcNow.AddMinutes(-10)),
            ("agent:ada", "MessageReceived", "Received msg", DateTimeOffset.UtcNow.AddMinutes(-9)),
            ("human:savasp", "MessageReceived", "Received msg", DateTimeOffset.UtcNow.AddMinutes(-8)),
        });
        await SeedThreadAsync("c-2", new[]
        {
            ("agent:grace", "ThreadStarted", "Started c-2", DateTimeOffset.UtcNow.AddMinutes(-20)),
            ("agent:grace", "ThreadCompleted", "Done", DateTimeOffset.UtcNow.AddMinutes(-1)),
        });

        var svc = new ThreadQueryService(_db);

        var result = await svc.ListAsync(new ThreadQueryFilters(), TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);
        var c1 = result.Single(r => r.Id == "c-1");
        c1.Status.ShouldBe("active");
        c1.Participants.ShouldContain("agent://ada");
        c1.Participants.ShouldContain("human://savasp");
        c1.EventCount.ShouldBe(3);

        var c2 = result.Single(r => r.Id == "c-2");
        c2.Status.ShouldBe("completed");
    }

    [Fact]
    public async Task ListAsync_AgentFilterBySlug_ResolvesThroughDirectoryAndMatchesActorIdParticipants()
    {
        // Production activity events carry the actor id (a UUID) as their
        // source — see AgentActor.EmitActivityEventAsync. The portal's
        // Messages tab and the CLI's `spring conversation list --agent
        // <name>` both pass the agent slug, so a literal slug-only filter
        // would return zero matches even when the thread clearly involves
        // the named agent. The directory resolves the slug to its actor id
        // and the filter matches against the resolved address.
        var actorId = "2ab56e09-6746-40b2-9a34-f0d6babfc0f3";
        await SeedThreadAsync("c-1", new[]
        {
            ($"agent:{actorId}", "MessageReceived", "Received human ask", DateTimeOffset.UtcNow.AddMinutes(-5)),
            ($"agent:{actorId}", "ThreadStarted", "Started c-1", DateTimeOffset.UtcNow.AddMinutes(-5)),
        });

        var directory = Substitute.For<IDirectoryService>();
        directory.ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "backend-engineer"),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                Address: new Address("agent", "backend-engineer"),
                ActorId: actorId,
                DisplayName: "backend-engineer",
                Description: string.Empty,
                Role: null,
                RegisteredAt: DateTimeOffset.UtcNow));

        var svc = new ThreadQueryService(_db, directory);

        var result = await svc.ListAsync(
            new ThreadQueryFilters(Agent: "backend-engineer"),
            TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("c-1");
    }

    [Fact]
    public async Task ListAsync_AgentFilter_NoDirectory_FallsBackToLiteralMatch()
    {
        // Tests in this suite seed events with the slug-form source
        // (`agent:ada`) — the existing assertions all rely on the literal
        // form matching, so injecting no directory must keep that path
        // working unchanged.
        await SeedThreadAsync("c-ada", new[]
        {
            ("agent:ada", "ThreadStarted", "Started c-ada", DateTimeOffset.UtcNow.AddMinutes(-5)),
        });

        var svc = new ThreadQueryService(_db);

        var result = await svc.ListAsync(
            new ThreadQueryFilters(Agent: "ada"),
            TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("c-ada");
    }

    [Fact]
    public async Task ListAsync_StatusFilter_SelectsOnlyMatchingThreads()
    {
        await SeedThreadAsync("c-active", new[]
        {
            ("agent:ada", "ThreadStarted", "", DateTimeOffset.UtcNow),
        });
        await SeedThreadAsync("c-done", new[]
        {
            ("agent:ada", "ThreadStarted", "", DateTimeOffset.UtcNow.AddMinutes(-5)),
            ("agent:ada", "ThreadCompleted", "", DateTimeOffset.UtcNow.AddMinutes(-1)),
        });

        var svc = new ThreadQueryService(_db);

        var completed = await svc.ListAsync(
            new ThreadQueryFilters(Status: "completed"),
            TestContext.Current.CancellationToken);

        completed.Count.ShouldBe(1);
        completed[0].Id.ShouldBe("c-done");
    }

    [Fact]
    public async Task GetAsync_ReturnsOrderedEventsForThread()
    {
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-10);
        var later = DateTimeOffset.UtcNow.AddMinutes(-1);
        await SeedThreadAsync("c-1", new[]
        {
            ("agent:ada", "MessageReceived", "later", later),
            ("agent:ada", "ThreadStarted", "first", earlier),
        });

        var svc = new ThreadQueryService(_db);
        var detail = await svc.GetAsync("c-1", TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail!.Events.Count.ShouldBe(2);
        // Events ordered oldest first.
        detail.Events[0].EventType.ShouldBe("ThreadStarted");
        detail.Events[1].EventType.ShouldBe("MessageReceived");
        detail.Summary.Id.ShouldBe("c-1");
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        var svc = new ThreadQueryService(_db);

        var detail = await svc.GetAsync("nope", TestContext.Current.CancellationToken);

        detail.ShouldBeNull();
    }

    [Fact]
    public async Task ListInboxAsync_HumanAwaitingAsk_AppearsOnce()
    {
        await SeedThreadAsync("c-1", new[]
        {
            ("agent:ada", "ThreadStarted", "Started", DateTimeOffset.UtcNow.AddMinutes(-10)),
            ("agent:ada", "MessageReceived", "Replied", DateTimeOffset.UtcNow.AddMinutes(-5)),
            ("human:savasp", "MessageReceived", "Approve merge?", DateTimeOffset.UtcNow.AddMinutes(-1)),
        });

        var svc = new ThreadQueryService(_db);
        var inbox = await svc.ListInboxAsync("human://savasp", null, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(1);
        inbox[0].ThreadId.ShouldBe("c-1");
        inbox[0].Human.ShouldBe("human://savasp");
        inbox[0].From.ShouldBe("agent://ada");
    }

    [Fact]
    public async Task ListInboxAsync_HumanAlreadyReplied_DropsRow()
    {
        // Last event on the thread is a MessageReceived NOT on the human →
        // inbox is empty because the human already said something after the ask.
        await SeedThreadAsync("c-1", new[]
        {
            ("human:savasp", "MessageReceived", "Question from agent", DateTimeOffset.UtcNow.AddMinutes(-10)),
            ("agent:ada", "MessageReceived", "Ack from human", DateTimeOffset.UtcNow.AddMinutes(-5)),
        });

        var svc = new ThreadQueryService(_db);
        var inbox = await svc.ListInboxAsync("human://savasp", null, TestContext.Current.CancellationToken);

        inbox.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListInboxAsync_DifferentHuman_DoesNotLeak()
    {
        await SeedThreadAsync("c-1", new[]
        {
            ("agent:ada", "ThreadStarted", "Started", DateTimeOffset.UtcNow.AddMinutes(-5)),
            ("human:alice", "MessageReceived", "For alice", DateTimeOffset.UtcNow.AddMinutes(-1)),
        });

        var svc = new ThreadQueryService(_db);
        var inbox = await svc.ListInboxAsync("human://savasp", null, TestContext.Current.CancellationToken);

        inbox.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListInboxAsync_FreshAgentReply_AppearsInInbox()
    {
        // Reproduces #1210: a fresh agent → human reply should land in the
        // inbox immediately. The on-the-wire event sequence emitted by the
        // platform on a normal "human asks agent / agent answers" turn is:
        //
        //   1. agent:<id> MessageReceived       (AgentActor saw the human's message)
        //   2. agent:<id> ConversationStarted   (HandleDomainMessageAsync, Case 1)
        //   3. agent:<id> StateChanged          (Idle → Active, Debug severity)
        //   4. human:<id> MessageReceived       (HumanActor saw the agent's reply)
        //
        // The inbox predicate keys off "last event is MessageReceived from
        // the caller's human address", so this conversation must show up.
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2);
        await SeedThreadAsync("e58eaf86", new[]
        {
            ("agent:backend-engineer", "MessageReceived", "Received Domain message from human://local-dev-user", t0),
            ("agent:backend-engineer", "ThreadStarted", "Started thread e58eaf86", t0.AddMilliseconds(1)),
            ("agent:backend-engineer", "StateChanged", "State changed from Idle to Active", t0.AddMilliseconds(2)),
            ("human:local-dev-user", "MessageReceived", "Received Domain message from agent://backend-engineer", t0.AddMinutes(1)),
        });

        var svc = new ThreadQueryService(_db);
        var inbox = await svc.ListInboxAsync("human://local-dev-user", null, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(1);
        inbox[0].ThreadId.ShouldBe("e58eaf86");
        inbox[0].Human.ShouldBe("human://local-dev-user");
        inbox[0].From.ShouldBe("agent://backend-engineer");
    }

    [Fact]
    public async Task ListInboxAsync_FreshReplyAlongsideStaleThread_BothAppearMostRecentFirst()
    {
        // Variant of #1210: the user reported that a stale conversation from
        // an earlier debug session was visible while a fresh reply was not.
        // Both rows must appear, with the fresh row sorted ahead of the
        // stale one (most recent PendingSince first).
        var stale = DateTimeOffset.UtcNow.AddMinutes(-90);
        var fresh = DateTimeOffset.UtcNow.AddMinutes(-1);
        await SeedThreadAsync("5925edfa", new[]
        {
            ("agent:debug-agent", "ThreadStarted", "Started 5925edfa", stale.AddMinutes(-1)),
            ("human:local-dev-user", "MessageReceived", "Stale ask", stale),
        });
        await SeedThreadAsync("e58eaf86", new[]
        {
            ("agent:backend-engineer", "MessageReceived", "Received Domain message", fresh.AddSeconds(-80)),
            ("agent:backend-engineer", "ThreadStarted", "Started e58eaf86", fresh.AddSeconds(-79)),
            ("human:local-dev-user", "MessageReceived", "Fresh reply", fresh),
        });

        var svc = new ThreadQueryService(_db);
        var inbox = await svc.ListInboxAsync("human://local-dev-user", null, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(2);
        inbox[0].ThreadId.ShouldBe("e58eaf86");
        inbox[1].ThreadId.ShouldBe("5925edfa");
    }

    [Fact]
    public async Task ListInboxAsync_TrailingStateChangedAfterHumanReceive_StillAppears()
    {
        // #1210 root cause: when the dispatch path emits a trailing event on
        // the same correlation id (e.g. StateChanged from
        // ClearActiveConversationAsync after a non-zero exit, future
        // observability events from extension plugins, etc.), the inbox
        // predicate must NOT drop the row. The human still hasn't replied,
        // and the row has to stay visible until they do.
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2);
        await SeedThreadAsync("c-trailing", new[]
        {
            ("agent:ada", "MessageReceived", "Agent received human's ask", t0),
            ("agent:ada", "ThreadStarted", "Started c-trailing", t0.AddMilliseconds(1)),
            ("human:savasp", "MessageReceived", "Agent's reply", t0.AddSeconds(80)),
            // Trailing event on the same conversation — e.g. a state
            // teardown emitted by the agent after routing the response.
            ("agent:ada", "StateChanged", "State changed from Active to Idle", t0.AddSeconds(81)),
        });

        var svc = new ThreadQueryService(_db);
        var inbox = await svc.ListInboxAsync("human://savasp", null, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(1);
        inbox[0].ThreadId.ShouldBe("c-trailing");
        inbox[0].From.ShouldBe("agent://ada");
    }

    [Fact]
    public async Task ListInboxAsync_HumanReceivedThenAgentReceivedAfter_DropsRow()
    {
        // The human replied — an agent emitted MessageReceived AFTER the
        // human's last MessageReceived on the same conversation. The
        // conversation must drop out of the inbox even if there are more
        // trailing events afterwards.
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedThreadAsync("c-replied", new[]
        {
            ("agent:ada", "MessageReceived", "Agent received human's first ask", t0),
            ("human:savasp", "MessageReceived", "Agent's reply", t0.AddSeconds(60)),
            ("agent:ada", "MessageReceived", "Agent received human's reply", t0.AddSeconds(120)),
            ("agent:ada", "StateChanged", "Trailing tail", t0.AddSeconds(121)),
        });

        var svc = new ThreadQueryService(_db);
        var inbox = await svc.ListInboxAsync("human://savasp", null, TestContext.Current.CancellationToken);

        inbox.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAsync_MessageReceivedWithBody_SurfacesBodyAndEnvelope()
    {
        // #1209: the projection should pull the message envelope (id /
        // from / to / body) out of the activity event Details JSON so
        // every conversation surface — CLI, portal, future bots — sees
        // the same shape.
        var messageId = Guid.NewGuid();
        var message = new Message(
            messageId,
            new Address("human", "savasp"),
            new Address("agent", "ada"),
            MessageType.Domain,
            "c-1",
            JsonSerializer.SerializeToElement("Approve merge?"),
            DateTimeOffset.UtcNow);

        _db.ActivityEvents.Add(new ActivityEventRecord
        {
            Id = Guid.NewGuid(),
            Source = "agent:ada",
            EventType = nameof(ActivityEventType.MessageReceived),
            Severity = "Info",
            Summary = $"Received Domain message {message.Id} from human://savasp",
            Details = MessageReceivedDetails.Build(message),
            CorrelationId = "c-1",
            Timestamp = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = new ThreadQueryService(_db);
        var detail = await svc.GetAsync("c-1", TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        var evt = detail!.Events.Single();
        evt.MessageId.ShouldBe(messageId);
        evt.From.ShouldBe("human://savasp");
        evt.To.ShouldBe("agent://ada");
        evt.Body.ShouldBe("Approve merge?");
    }

    // --- UnreadCount tests (#1477) ---

    [Fact]
    public async Task ListInboxAsync_NullLastReadAt_UnreadCountEqualsAllEvents()
    {
        // With no lastReadAt supplied, cursor is DateTimeOffset.MinValue so
        // all events count as unread.
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedThreadAsync("c-unread", new[]
        {
            ("agent:ada", "ThreadStarted", "Started", t0),
            ("agent:ada", "MessageReceived", "Agent msg", t0.AddSeconds(30)),
            ("human:savasp", "MessageReceived", "Agent's reply to human", t0.AddSeconds(60)),
        });

        var svc = new ThreadQueryService(_db);
        var inbox = await svc.ListInboxAsync("human://savasp", null, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(1);
        // All 3 events are "after MinValue" → UnreadCount = 3.
        inbox[0].UnreadCount.ShouldBe(3);
    }

    [Fact]
    public async Task ListInboxAsync_WithLastReadAt_UnreadCountReflectsEventsSince()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedThreadAsync("c-partial", new[]
        {
            ("agent:ada", "ThreadStarted", "Started", t0),
            ("agent:ada", "MessageReceived", "Agent msg", t0.AddSeconds(30)),
            ("human:savasp", "MessageReceived", "Reply", t0.AddSeconds(60)),
        });

        var svc = new ThreadQueryService(_db);

        // The human last read the thread after the first 2 events — only the
        // third event (human MessageReceived at t0+60s) is "new".
        var lastReadAt = new Dictionary<string, DateTimeOffset>
        {
            ["c-partial"] = t0.AddSeconds(45),
        };

        var inbox = await svc.ListInboxAsync("human://savasp", lastReadAt, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(1);
        inbox[0].UnreadCount.ShouldBe(1);
    }

    [Fact]
    public async Task ListInboxAsync_FullyReadThread_UnreadCountIsZero()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedThreadAsync("c-read", new[]
        {
            ("agent:ada", "ThreadStarted", "Started", t0),
            ("agent:ada", "MessageReceived", "Agent msg", t0.AddSeconds(30)),
            ("human:savasp", "MessageReceived", "Reply", t0.AddSeconds(60)),
        });

        var svc = new ThreadQueryService(_db);

        // lastReadAt is after all events → UnreadCount = 0.
        var lastReadAt = new Dictionary<string, DateTimeOffset>
        {
            ["c-read"] = t0.AddSeconds(120),
        };

        var inbox = await svc.ListInboxAsync("human://savasp", lastReadAt, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(1);
        inbox[0].UnreadCount.ShouldBe(0);
    }

    // --- NormaliseSource identity-form tests (#1490) ---

    [Fact]
    public void NormaliseSource_AgentWithUuidPath_EmitsIdentityForm()
    {
        // Activity events for agents are stored as "agent:<uuid>".
        // NormaliseSource must upgrade these to "agent:id:<uuid>".
        var actorId = "2ab56e09-6746-40b2-9a34-f0d6babfc0f3";
        ThreadQueryService.NormaliseSource($"agent:{actorId}")
            .ShouldBe($"agent:id:{actorId}");
    }

    [Fact]
    public void NormaliseSource_UnitWithUuidPath_EmitsIdentityForm()
    {
        var actorId = "4c5d6e7f-0000-0000-0000-000000000001";
        ThreadQueryService.NormaliseSource($"unit:{actorId}")
            .ShouldBe($"unit:id:{actorId}");
    }

    [Fact]
    public void NormaliseSource_AgentWithSlugPath_EmitsNavigationForm()
    {
        // Non-UUID paths are kept in the navigation form.
        ThreadQueryService.NormaliseSource("agent:backend-engineer")
            .ShouldBe("agent://backend-engineer");
    }

    [Fact]
    public void NormaliseSource_HumanWithAnyPath_EmitsNavigationForm()
    {
        // Human sources always use the navigation form until #1491 lands.
        ThreadQueryService.NormaliseSource("human:savasp")
            .ShouldBe("human://savasp");
    }

    [Fact]
    public void NormaliseSource_AlreadyInIdentityForm_Passthrough()
    {
        var actorId = "2ab56e09-6746-40b2-9a34-f0d6babfc0f3";
        var source = $"agent:id:{actorId}";
        ThreadQueryService.NormaliseSource(source).ShouldBe(source);
    }

    [Fact]
    public void NormaliseSource_AlreadyInNavigationForm_Passthrough()
    {
        ThreadQueryService.NormaliseSource("agent://ada").ShouldBe("agent://ada");
    }

    [Fact]
    public async Task ListAsync_AgentSourceWithUuidPath_ParticipantInIdentityForm()
    {
        // When activity events carry "agent:<uuid>" as the source, the
        // participants list on the thread summary must expose the identity form
        // "agent:id:<uuid>", not the navigation form "agent://<uuid>".
        var actorId = "2ab56e09-6746-40b2-9a34-f0d6babfc0f3";
        await SeedThreadAsync("c-uuid", new[]
        {
            ($"agent:{actorId}", "ThreadStarted", "Started", DateTimeOffset.UtcNow.AddMinutes(-5)),
            ($"agent:{actorId}", "MessageReceived", "Replied", DateTimeOffset.UtcNow.AddMinutes(-3)),
            ("human:savasp", "MessageReceived", "Ask", DateTimeOffset.UtcNow.AddMinutes(-1)),
        });

        var svc = new ThreadQueryService(_db);
        var result = await svc.ListAsync(new ThreadQueryFilters(), TestContext.Current.CancellationToken);

        var thread = result.Single(t => t.Id == "c-uuid");
        // Agent participant must be the identity form.
        thread.Participants.ShouldContain($"agent:id:{actorId}");
        // Human participant stays in navigation form until #1491.
        thread.Participants.ShouldContain("human://savasp");
        // The slug-shaped navigation form must NOT appear for UUID-path sources.
        thread.Participants.ShouldNotContain($"agent://{actorId}");
    }

    [Fact]
    public async Task ListInboxAsync_AgentSourceWithUuidPath_FromInIdentityForm()
    {
        var actorId = "4c5d6e7f-0000-0000-0000-000000000002";
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedThreadAsync("c-inbox-uuid", new[]
        {
            ($"agent:{actorId}", "ThreadStarted", "Started", t0),
            ($"agent:{actorId}", "MessageReceived", "Replied", t0.AddSeconds(30)),
            ("human:savasp", "MessageReceived", "Ask", t0.AddSeconds(60)),
        });

        var svc = new ThreadQueryService(_db);
        var inbox = await svc.ListInboxAsync("human://savasp", null, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(1);
        // The "from" field must be the stable identity form.
        inbox[0].From.ShouldBe($"agent:id:{actorId}");
    }

    [Fact]
    public async Task ListAsync_AgentFilterBySlug_IdentityFormNeedleMatchesUuidSourceEvents()
    {
        // Regression guard for #1490: when the directory resolves a slug
        // to a UUID, the filter needle must be in the identity form
        // ("agent:id:<uuid>") to match participants that NormaliseSource
        // upgraded to the same form.
        var actorId = "2ab56e09-6746-40b2-9a34-f0d6babfc0f3";
        await SeedThreadAsync("c-id-filter", new[]
        {
            ($"agent:{actorId}", "MessageReceived", "Msg", DateTimeOffset.UtcNow.AddMinutes(-5)),
        });

        var directory = Substitute.For<IDirectoryService>();
        directory.ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "backend-engineer"),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                Address: new Address("agent", "backend-engineer"),
                ActorId: actorId,
                DisplayName: "backend-engineer",
                Description: string.Empty,
                Role: null,
                RegisteredAt: DateTimeOffset.UtcNow));

        var svc = new ThreadQueryService(_db, directory);

        var result = await svc.ListAsync(
            new ThreadQueryFilters(Agent: "backend-engineer"),
            TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("c-id-filter");
    }

    [Fact]
    public void ToPersistenceSource_IdentityForm_ConvertsToCompactForm()
    {
        // "agent:id:<uuid>" → "agent:<uuid>" (the compact persistence form).
        var actorId = "2ab56e09-6746-40b2-9a34-f0d6babfc0f3";
        ThreadQueryService.ToPersistenceSource($"agent:id:{actorId}")
            .ShouldBe($"agent:{actorId}");
    }

    [Fact]
    public void ToPersistenceSource_NavigationForm_ConvertsToCompactForm()
    {
        ThreadQueryService.ToPersistenceSource("human://savasp").ShouldBe("human:savasp");
    }

    [Fact]
    public void ToPersistenceSource_AlreadyCompact_Passthrough()
    {
        ThreadQueryService.ToPersistenceSource("human:savasp").ShouldBe("human:savasp");
    }

    private async Task SeedThreadAsync(
        string threadId,
        (string source, string eventType, string summary, DateTimeOffset ts)[] events)
    {
        foreach (var (source, type, summary, ts) in events)
        {
            _db.ActivityEvents.Add(new ActivityEventRecord
            {
                Id = Guid.NewGuid(),
                Source = source,
                EventType = type,
                Severity = "Info",
                Summary = summary,
                Timestamp = ts,
                CorrelationId = threadId,
            });
        }
        await _db.SaveChangesAsync();
    }
}