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

    /// <summary>
    /// Tracks the seed-time scheme + slug for every actor id that
    /// <see cref="SeedThreadAsync"/> writes, so <see cref="BuildDirectory"/>
    /// can hand the projection a real <c>actorId → DirectoryEntry</c> map.
    /// Without it the post-#1629 projection collapses every source to
    /// <c>unknown:&lt;hex&gt;</c> and slug-form filter assertions stop
    /// working.
    /// </summary>
    private readonly Dictionary<Guid, (string Scheme, string Slug)> _seededActors = new();

    public ThreadQueryServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase($"ThreadQueryTest-{Guid.NewGuid()}")
            .Options;
        _db = new SpringDbContext(dbOptions);
    }

    private ThreadQueryService BuildService()
    {
        return new ThreadQueryService(_db, BuildDirectory());
    }

    private IDirectoryService BuildDirectory()
    {
        var dir = Substitute.For<IDirectoryService>();
        var entries = _seededActors
            .Select(kvp => new DirectoryEntry(
                Address: new Address(kvp.Value.Scheme, kvp.Key),
                ActorId: kvp.Key,
                DisplayName: kvp.Value.Slug,
                Description: string.Empty,
                Role: null,
                RegisteredAt: DateTimeOffset.UtcNow))
            .ToList();
        dir.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DirectoryEntry>>(entries));
        dir.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var addr = ci.Arg<Address>();
                var match = entries.FirstOrDefault(
                    e => e.Address.Scheme == addr.Scheme && e.ActorId == addr.Id);
                return Task.FromResult<DirectoryEntry?>(match);
            });
        return dir;
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ListAsync_NoActivity_ReturnsEmpty()
    {
        var svc = BuildService();

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

        var svc = BuildService();

        var result = await svc.ListAsync(new ThreadQueryFilters(), TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);
        var c1 = result.Single(r => r.Id == "c-1");
        c1.Status.ShouldBe("active");
        c1.Participants.ShouldContain($"agent:id:{TestSlugIds.HexFor("ada")}");
        c1.Participants.ShouldContain($"human:id:{TestSlugIds.HexFor("savasp")}");
        c1.EventCount.ShouldBe(3);

        var c2 = result.Single(r => r.Id == "c-2");
        c2.Status.ShouldBe("completed");
    }

    // #1629: ListAsync_AgentFilterBySlug_ResolvesThroughDirectoryAndMatchesActorIdParticipants
    // and ListAsync_AgentFilterBySlug_IdentityFormNeedleMatchesUuidSourceEvents were
    // slug-resolution tests; post #1629 every address is identity, so the slug
    // upgrade path the tests exercised no longer exists.

    [Fact]
    public async Task ListAsync_AgentFilter_WithDirectory_ResolvesGuidNeedle()
    {
        // Post-#1629 every address is a Guid; the projection renders the
        // scheme via IDirectoryService and the agent filter resolves the
        // caller-supplied Guid through the same directory. This is the
        // happy-path replacement for the slug-resolution tests retired
        // earlier in this file.
        var adaHex = TestSlugIds.HexFor("ada");
        await SeedThreadAsync("c-ada", new[]
        {
            ($"agent:{adaHex}", "ThreadStarted", "Started c-ada", DateTimeOffset.UtcNow.AddMinutes(-5)),
        });

        var svc = BuildService();

        var result = await svc.ListAsync(
            new ThreadQueryFilters(Agent: adaHex),
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

        var svc = BuildService();

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

        var svc = BuildService();
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
        var svc = BuildService();

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

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync($"human:id:{TestSlugIds.HexFor("savasp")}", null, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(1);
        inbox[0].ThreadId.ShouldBe("c-1");
        inbox[0].Human.ShouldBe($"human:id:{TestSlugIds.HexFor("savasp")}");
        inbox[0].From.ShouldBe($"agent:id:{TestSlugIds.HexFor("ada")}");
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

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync($"human:id:{TestSlugIds.HexFor("savasp")}", null, TestContext.Current.CancellationToken);

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

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync($"human:id:{TestSlugIds.HexFor("savasp")}", null, TestContext.Current.CancellationToken);

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
        // #1636: actors emit the message body (or a short placeholder) as the
        // summary now — never the legacy "Received Domain message <uuid>
        // from <address>" envelope. Test seeds mirror that.
        await SeedThreadAsync("e58eaf86", new[]
        {
            ("agent:backend-engineer", "MessageReceived", "Hello, agent.", t0),
            ("agent:backend-engineer", "ThreadStarted", "Started thread e58eaf86", t0.AddMilliseconds(1)),
            ("agent:backend-engineer", "StateChanged", "State changed from Idle to Active", t0.AddMilliseconds(2)),
            ("human:local-dev-user", "MessageReceived", "On it.", t0.AddMinutes(1)),
        });

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync($"human:id:{TestSlugIds.HexFor("local-dev-user")}", null, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(1);
        inbox[0].ThreadId.ShouldBe("e58eaf86");
        inbox[0].Human.ShouldBe($"human:id:{TestSlugIds.HexFor("local-dev-user")}");
        inbox[0].From.ShouldBe($"agent:id:{TestSlugIds.HexFor("backend-engineer")}");
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
        // #1636: production never writes "Received Domain message" — the
        // summary is the message body or a short placeholder.
        await SeedThreadAsync("e58eaf86", new[]
        {
            ("agent:backend-engineer", "MessageReceived", "Message received", fresh.AddSeconds(-80)),
            ("agent:backend-engineer", "ThreadStarted", "Started e58eaf86", fresh.AddSeconds(-79)),
            ("human:local-dev-user", "MessageReceived", "Fresh reply", fresh),
        });

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync($"human:id:{TestSlugIds.HexFor("local-dev-user")}", null, TestContext.Current.CancellationToken);

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

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync($"human:id:{TestSlugIds.HexFor("savasp")}", null, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(1);
        inbox[0].ThreadId.ShouldBe("c-trailing");
        inbox[0].From.ShouldBe($"agent:id:{TestSlugIds.HexFor("ada")}");
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

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync($"human:id:{TestSlugIds.HexFor("savasp")}", null, TestContext.Current.CancellationToken);

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
            Address.For("human", TestSlugIds.HexFor("savasp")),
            Address.For("agent", TestSlugIds.HexFor("ada")),
            MessageType.Domain,
            "c-1",
            JsonSerializer.SerializeToElement("Approve merge?"),
            DateTimeOffset.UtcNow);

        _db.ActivityEvents.Add(new ActivityEventRecord
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            EventType = nameof(ActivityEventType.MessageReceived),
            Severity = "Info",
            // #1636: actors write the body (or a short placeholder) — never
            // the legacy "Received Domain message …" envelope.
            Summary = MessageReceivedDetails.BuildSummary(message),
            Details = MessageReceivedDetails.Build(message),
            CorrelationId = "c-1",
            Timestamp = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = BuildService();
        var detail = await svc.GetAsync("c-1", TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        var evt = detail!.Events.Single();
        evt.MessageId.ShouldBe(messageId);
        evt.From.ShouldBe($"human://{TestSlugIds.HexFor("savasp")}");
        evt.To.ShouldBe($"agent://{TestSlugIds.HexFor("ada")}");
        evt.Body.ShouldBe("Approve merge?");
    }

    [Fact]
    public async Task GetAsync_AgentReplyEnvelope_ExtractsOutputAsBody()
    {
        // #1547 / #1549: agent replies are routed back as
        // { Output: "<text>", ExitCode: 0 } objects (the
        // A2AExecutionDispatcher response shape), so MessageReceivedDetails
        // must surface the Output string as the message body — otherwise the
        // recipient's MessageReceived event has a null body and the portal
        // bubble falls back to the summary line. #1636: the summary is now
        // body-as-text or a short placeholder, never the GUID envelope.
        var messageId = Guid.NewGuid();
        var replyPayload = JsonSerializer.SerializeToElement(new
        {
            Output = "Merge approved — looks good to ship.",
            ExitCode = 0,
        });
        var message = new Message(
            messageId,
            Address.For("agent", TestSlugIds.HexFor("ada")),
            Address.For("human", TestSlugIds.HexFor("savasp")),
            MessageType.Domain,
            "c-reply",
            replyPayload,
            DateTimeOffset.UtcNow);

        _db.ActivityEvents.Add(new ActivityEventRecord
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            EventType = nameof(ActivityEventType.MessageReceived),
            Severity = "Info",
            Summary = MessageReceivedDetails.BuildSummary(message),
            Details = MessageReceivedDetails.Build(message),
            CorrelationId = "c-reply",
            Timestamp = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = BuildService();
        var detail = await svc.GetAsync("c-reply", TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        var evt = detail!.Events.Single();
        evt.Body.ShouldBe("Merge approved — looks good to ship.");
    }

    [Fact]
    public async Task GetAsync_LegacyDetailsWithoutBody_FallsBackToPayloadExtraction()
    {
        // Older events (persisted before #1551 extended TryExtractText) have a
        // Details blob with `payload` but no `body`. The projection must
        // re-extract from `payload` so already-stored agent replies surface as
        // bubble bodies. #1636: legacy rows may also carry the old
        // "Received Domain message …" envelope as the summary — the projection
        // must still surface a usable body from `payload` regardless.
        var messageId = Guid.NewGuid();
        var adaHex = TestSlugIds.HexFor("ada");
        var savaspHex = TestSlugIds.HexFor("savasp");
        var details = JsonDocument.Parse(
            $$"""
            {
                "messageId": "{{messageId}}",
                "from": "agent:id:{{adaHex}}",
                "to": "human:id:{{savaspHex}}",
                "messageType": "Domain",
                "payload": {
                    "Output": "Reply text from before the fix.",
                    "ExitCode": 0
                }
            }
            """).RootElement.Clone();

        _db.ActivityEvents.Add(new ActivityEventRecord
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            EventType = nameof(ActivityEventType.MessageReceived),
            Severity = "Info",
            // Legacy on-disk shape: the envelope template that production no
            // longer writes (#1636) but pre-fix activity rows still carry.
            Summary = $"Received Domain message {messageId} from agent://ada",
            Details = details,
            CorrelationId = "c-legacy",
            Timestamp = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = BuildService();
        var detail = await svc.GetAsync("c-legacy", TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        var evt = detail!.Events.Single();
        evt.Body.ShouldBe("Reply text from before the fix.");
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

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync($"human:id:{TestSlugIds.HexFor("savasp")}", null, TestContext.Current.CancellationToken);

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

        var svc = BuildService();

        // The human last read the thread after the first 2 events — only the
        // third event (human MessageReceived at t0+60s) is "new".
        var lastReadAt = new Dictionary<string, DateTimeOffset>
        {
            ["c-partial"] = t0.AddSeconds(45),
        };

        var inbox = await svc.ListInboxAsync($"human:id:{TestSlugIds.HexFor("savasp")}", lastReadAt, TestContext.Current.CancellationToken);

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

        var svc = BuildService();

        // lastReadAt is after all events → UnreadCount = 0.
        var lastReadAt = new Dictionary<string, DateTimeOffset>
        {
            ["c-read"] = t0.AddSeconds(120),
        };

        var inbox = await svc.ListInboxAsync($"human:id:{TestSlugIds.HexFor("savasp")}", lastReadAt, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(1);
        inbox[0].UnreadCount.ShouldBe(0);
    }

    // --- NormaliseSource identity-form tests (#1490) ---

    [Fact]
    public void NormaliseSource_AgentWithUuidPath_EmitsIdentityForm()
    {
        // Activity events for agents are stored as "agent:<uuid>".
        // NormaliseSource must upgrade these to "agent:id:<uuid>" using the
        // canonical no-dash 32-char form (#1629).
        var actorId = "2ab56e09-6746-40b2-9a34-f0d6babfc0f3";
        var noDash = Guid.Parse(actorId).ToString("N");
        ThreadQueryService.NormaliseSource($"agent:{actorId}")
            .ShouldBe($"agent:id:{noDash}");
    }

    [Fact]
    public void NormaliseSource_UnitWithUuidPath_EmitsIdentityForm()
    {
        var actorId = "4c5d6e7f-0000-0000-0000-000000000001";
        var noDash = Guid.Parse(actorId).ToString("N");
        ThreadQueryService.NormaliseSource($"unit:{actorId}")
            .ShouldBe($"unit:id:{noDash}");
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
        // Non-UUID human sources are kept in the navigation form (legacy
        // pre-#1491 events still surface this way).
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
        ThreadQueryService.NormaliseSource($"agent:id:{TestSlugIds.HexFor("ada")}").ShouldBe($"agent:id:{TestSlugIds.HexFor("ada")}");
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

        var svc = BuildService();
        var result = await svc.ListAsync(new ThreadQueryFilters(), TestContext.Current.CancellationToken);

        var thread = result.Single(t => t.Id == "c-uuid");
        // Agent participant must be the identity form (no-dash 32-char hex).
        thread.Participants.ShouldContain($"agent:id:{Guid.Parse(actorId):N}");
        // Human participant stays in navigation form until #1491.
        thread.Participants.ShouldContain($"human:id:{TestSlugIds.HexFor("savasp")}");
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

        var svc = BuildService();
        var inbox = await svc.ListInboxAsync($"human:id:{TestSlugIds.HexFor("savasp")}", null, TestContext.Current.CancellationToken);

        inbox.Count.ShouldBe(1);
        // The "from" field must be the stable identity form (no-dash hex, #1629).
        inbox[0].From.ShouldBe($"agent:id:{Guid.Parse(actorId):N}");
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
        var savaspHex = TestSlugIds.HexFor("savasp").ToString();
        ThreadQueryService.ToPersistenceSource($"human://{savaspHex}").ShouldBe($"human:{savaspHex}");
    }

    [Fact]
    public void ToPersistenceSource_AlreadyCompact_Passthrough()
    {
        var savaspHex = TestSlugIds.HexFor("savasp").ToString();
        ThreadQueryService.ToPersistenceSource($"human:{savaspHex}").ShouldBe($"human:{savaspHex}");
    }

    /// <summary>
    /// Best-effort parse of legacy "scheme:&lt;guid-or-slug&gt;" test seed strings into
    /// the <see cref="ActivityEventRecord.SourceId"/> Guid. When the slug part
    /// is not a Guid, we map it through <see cref="TestSlugIds.For"/> so that
    /// the same slug always produces the same Guid (otherwise repeated seeds
    /// for the same legacy participant would each get a fresh Guid and break
    /// the production query's grouping/filter logic).
    /// </summary>
    private (Guid Id, string Scheme, string Slug) ParseSource(string source)
    {
        var sepIdx = source.IndexOf(':');
        var scheme = sepIdx >= 0 ? source[..sepIdx] : "agent";
        var idPart = sepIdx >= 0 ? source[(sepIdx + 1)..] : source;
        if (Guid.TryParse(idPart, out var g))
        {
            return (g, scheme, idPart);
        }
        var guid = TestSlugIds.For(idPart);
        return (guid, scheme, idPart);
    }

    private async Task SeedThreadAsync(
        string threadId,
        (string source, string eventType, string summary, DateTimeOffset ts)[] events)
    {
        foreach (var (source, type, summary, ts) in events)
        {
            var (sourceId, scheme, slug) = ParseSource(source);
            _seededActors.TryAdd(sourceId, (scheme, slug));
            _db.ActivityEvents.Add(new ActivityEventRecord
            {
                Id = Guid.NewGuid(),
                SourceId = sourceId,
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