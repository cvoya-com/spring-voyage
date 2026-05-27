// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Threads;

using System.Text.Json;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Messaging.Rendering;
using Cvoya.Spring.Core.Messaging.Rendering.Renderers;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Dapr.Threads;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="EfMessageWriter"/>: per-message insert at dispatch
/// time, idempotency on re-dispatch, tenant isolation via the DbContext
/// query filter, parent-thread <c>last_activity_at</c> bump, and the skip
/// path for non-Domain / no-thread-id messages (#2053 / ADR-0030 / ADR-0040).
/// </summary>
public class EfMessageWriterTests : IDisposable
{
    private static readonly Guid Tenant1 = new("11111111-2222-3333-4444-000000000011");
    private static readonly Guid Tenant2 = new("11111111-2222-3333-4444-000000000012");

    private static readonly Address Human1 = new("human", new Guid("aaaa1001-0000-0000-0000-000000000001"));
    private static readonly Address Agent1 = new("agent", new Guid("aaaa1002-0000-0000-0000-000000000001"));

    private readonly DbContextOptions<SpringDbContext> _dbOptions;

    public EfMessageWriterTests()
    {
        _dbOptions = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    [Fact]
    public async Task WriteAsync_DomainMessage_PersistsRowWithEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var (writer, db, threadId, _) = await SetupAsync(Tenant1, Human1, Agent1, ct);

        var message = NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("hello world"));

        await writer.WriteAsync(message, ct);

        var row = await db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == message.Id, ct);
        row.ShouldNotBeNull();
        row!.TenantId.ShouldBe(Tenant1);
        row.ThreadId.ShouldBe(ParseGuid(threadId));
        row.SenderScheme.ShouldBe("human");
        row.SenderId.ShouldBe(Human1.Id);
        row.RecipientScheme.ShouldBe("agent");
        row.RecipientId.ShouldBe(Agent1.Id);
        row.MessageType.ShouldBe(nameof(MessageType.Domain));
        row.Body.ShouldBe("hello world");
        row.SentAt.ShouldBe(message.Timestamp);
        row.RetractedAt.ShouldBeNull();
        row.Payload.ShouldContain("hello world");
    }

    [Fact]
    public async Task WriteAsync_BumpsParentThreadLastActivityAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (writer, db, threadId, beforeWrite) = await SetupAsync(Tenant1, Human1, Agent1, ct);

        // Use a timestamp comfortably after the thread's CreatedAt so the
        // forward-only bump kicks in even on fast in-memory DB inserts.
        var sentAt = beforeWrite.AddMinutes(5);
        var message = NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("ping"), sentAt);

        await writer.WriteAsync(message, ct);

        var thread = await db.Threads.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == ParseGuid(threadId), ct);
        thread.ShouldNotBeNull();
        thread!.LastActivityAt.ShouldBe(sentAt);
    }

    [Fact]
    public async Task WriteAsync_LastActivityAtIsForwardOnly()
    {
        // Out-of-order writes must not move last_activity_at backwards —
        // the inbox surface wants the latest activity, not the most recently
        // saved row.
        var ct = TestContext.Current.CancellationToken;
        var (writer, db, threadId, baseline) = await SetupAsync(Tenant1, Human1, Agent1, ct);

        var newer = baseline.AddMinutes(10);
        var older = baseline.AddMinutes(2);

        await writer.WriteAsync(
            NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("first"), newer),
            ct);
        await writer.WriteAsync(
            NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("second"), older),
            ct);

        var thread = await db.Threads.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == ParseGuid(threadId), ct);
        thread.ShouldNotBeNull();
        thread!.LastActivityAt.ShouldBe(newer);
    }

    [Fact]
    public async Task WriteAsync_ReDispatchSameMessageId_IsNoOp()
    {
        // The writer must be idempotent: a manual retry of the same Message
        // (same Id) does not duplicate history. The post-condition is "exactly
        // one row exists with that id."
        var ct = TestContext.Current.CancellationToken;
        var (writer, db, threadId, _) = await SetupAsync(Tenant1, Human1, Agent1, ct);
        var message = NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("dup"));

        await writer.WriteAsync(message, ct);
        await writer.WriteAsync(message, ct);

        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.Id == message.Id)
            .ToListAsync(ct);
        rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task WriteAsync_NonDomainMessage_IsSkipped()
    {
        // ShouldWrite filters control messages — they have no thread-id and
        // never participate in the conversation timeline.
        var ct = TestContext.Current.CancellationToken;
        var (writer, db, threadId, _) = await SetupAsync(Tenant1, Human1, Agent1, ct);

        var control = new Message(
            Guid.NewGuid(),
            Human1, Agent1,
            MessageType.HealthCheck,
            threadId,
            default,
            DateTimeOffset.UtcNow);

        await writer.WriteAsync(control, ct);

        (await db.Messages.AsNoTracking().AnyAsync(m => m.Id == control.Id, ct))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task WriteAsync_DomainMessageWithoutThreadId_IsSkipped()
    {
        // ShouldWrite also blocks Domain messages without a Guid-shaped
        // thread id. The API path validates the shape before calling, but
        // the writer is defensive — a stray caller does not corrupt the FK.
        var ct = TestContext.Current.CancellationToken;
        var (writer, db, _, _) = await SetupAsync(Tenant1, Human1, Agent1, ct);

        var noThread = new Message(
            Guid.NewGuid(),
            Human1, Agent1,
            MessageType.Domain,
            ThreadId: null,
            JsonSerializer.SerializeToElement("orphan"),
            DateTimeOffset.UtcNow);

        await writer.WriteAsync(noThread, ct);

        (await db.Messages.AsNoTracking().AnyAsync(m => m.Id == noThread.Id, ct))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task WriteAsync_TenantIsolated_OtherTenantCannotSeeRow()
    {
        var ct = TestContext.Current.CancellationToken;

        var (tenant1Writer, tenant1Db, threadId, _) = await SetupAsync(Tenant1, Human1, Agent1, ct);
        var message = NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("private"));
        await tenant1Writer.WriteAsync(message, ct);

        // A separate tenant context bound to the same in-memory DB sees no
        // rows because the DbContext-level query filter scopes reads to
        // CurrentTenantId.
        using var tenant2Db = new SpringDbContext(_dbOptions, new StaticTenantContext(Tenant2));
        var visibleToTenant2 = await tenant2Db.Messages.AsNoTracking()
            .AnyAsync(m => m.Id == message.Id, ct);
        visibleToTenant2.ShouldBeFalse();

        // Bypassing the filter (reading "as the SQL row would") returns the
        // row — the data is there, it is just out of scope for tenant 2.
        var visibleAcrossTenants = await tenant2Db.Messages.IgnoreQueryFilters()
            .AnyAsync(m => m.Id == message.Id, ct);
        visibleAcrossTenants.ShouldBeTrue();
    }

    [Fact]
    public async Task WriteAsync_PersistsRetractionColumnAsNullByDefault()
    {
        // The retracted_at column is reserved for the retraction follow-up;
        // inserts on the dispatch path always leave it null.
        var ct = TestContext.Current.CancellationToken;
        var (writer, db, threadId, _) = await SetupAsync(Tenant1, Human1, Agent1, ct);
        var message = NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("body"));

        await writer.WriteAsync(message, ct);

        var row = await db.Messages.AsNoTracking().FirstAsync(m => m.Id == message.Id, ct);
        row.RetractedAt.ShouldBeNull();
    }

    [Fact]
    public async Task WriteAsync_OrderingByTenantThreadSentAt()
    {
        // Tests the read shape the timeline query depends on: rows for a
        // (tenant, thread) ordered by sent_at. Three writes spaced one
        // minute apart must come back in chronological order.
        var ct = TestContext.Current.CancellationToken;
        var (writer, db, threadId, baseline) = await SetupAsync(Tenant1, Human1, Agent1, ct);
        var threadGuid = ParseGuid(threadId);

        var first = NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("a"), baseline.AddMinutes(1));
        var second = NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("b"), baseline.AddMinutes(2));
        var third = NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("c"), baseline.AddMinutes(3));

        // Write out of order to make sure ordering depends on sent_at, not
        // insertion order.
        await writer.WriteAsync(third, ct);
        await writer.WriteAsync(first, ct);
        await writer.WriteAsync(second, ct);

        var ordered = await db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == threadGuid)
            .OrderBy(m => m.SentAt)
            .Select(m => m.Body)
            .ToListAsync(ct);

        ordered.ShouldBe(new[] { "a", "b", "c" });
    }

    [Fact]
    public async Task WriteAsync_CapturesRealDisplayNamesIntoParticipantNameSnapshots()
    {
        // #2533: every successful write upserts the sender's and
        // recipient's current real display names into the parent
        // thread's participant_name_snapshots map. Per-scheme fallbacks
        // never overwrite a captured real name.
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FixedNameParticipantDisplayNameResolver(new Dictionary<string, string>
        {
            [Human1.ToString()] = "Savas",
            [Agent1.ToString()] = "Ada Lovelace",
        });
        var (writer, db, threadId, _) = await SetupAsync(Tenant1, Human1, Agent1, ct, resolver);

        var message = NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("hi"));
        await writer.WriteAsync(message, ct);

        var thread = await db.Threads.AsNoTracking()
            .FirstAsync(t => t.Id == ParseGuid(threadId), ct);
        var snapshots = ParticipantNameSnapshotJson.Read(thread.ParticipantNameSnapshots);
        snapshots[Human1.ToString()].ShouldBe("Savas");
        snapshots[Agent1.ToString()].ShouldBe("Ada Lovelace");
    }

    [Fact]
    public async Task WriteAsync_FallbackDisplayName_DoesNotOverwriteCapturedRealName()
    {
        // The snapshot is the last *real* name seen, not the most recent
        // resolver output: a later write that hits the per-scheme generic
        // ("an agent", "a connector") must leave a previously-captured
        // real name in place. This is the whole point — once the agent
        // is soft-deleted the resolver only returns fallbacks; the
        // snapshot has to survive that transition.
        var ct = TestContext.Current.CancellationToken;
        var realNameResolver = new FixedNameParticipantDisplayNameResolver(new Dictionary<string, string>
        {
            [Human1.ToString()] = "Savas",
            [Agent1.ToString()] = "Ada Lovelace",
        });
        var (writer, db, threadId, _) = await SetupAsync(Tenant1, Human1, Agent1, ct, realNameResolver);

        await writer.WriteAsync(
            NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("first")),
            ct);

        // Subsequent write uses a resolver that only knows the human —
        // mirroring a soft-delete of the agent's definition row.
        var halfRealResolver = new FixedNameParticipantDisplayNameResolver(new Dictionary<string, string>
        {
            [Human1.ToString()] = "Savas",
        });
        var degradedDb = new SpringDbContext(_dbOptions, new StaticTenantContext(Tenant1));
        var degradedWriter = new EfMessageWriter(degradedDb, new StaticTenantContext(Tenant1), halfRealResolver, BuildPayloadRenderers(), NullLoggerFactory.Instance);
        await degradedWriter.WriteAsync(
            NewDomainMessage(Human1, Agent1, threadId, JsonSerializer.SerializeToElement("after delete")),
            ct);

        var thread = await db.Threads.AsNoTracking()
            .FirstAsync(t => t.Id == ParseGuid(threadId), ct);
        var snapshots = ParticipantNameSnapshotJson.Read(thread.ParticipantNameSnapshots);
        snapshots[Human1.ToString()].ShouldBe("Savas");
        snapshots[Agent1.ToString()].ShouldBe("Ada Lovelace");
    }

    [Fact]
    public async Task WriteAsync_StructuredPayload_BodyIsNullPayloadIsSerialised()
    {
        // Non-text payloads — the recipient-side reply shape is e.g.
        // { "Output": "...", "ExitCode": 0 }. The writer extracts Output for
        // the body and persists the whole thing as jsonb. Pure structured
        // payloads with no Output have a null Body and the payload column
        // carries the full JSON.
        var ct = TestContext.Current.CancellationToken;
        var (writer, db, threadId, _) = await SetupAsync(Tenant1, Human1, Agent1, ct);

        var structured = JsonSerializer.SerializeToElement(new { foo = "bar", count = 7 });
        var message = NewDomainMessage(Human1, Agent1, threadId, structured);

        await writer.WriteAsync(message, ct);

        var row = await db.Messages.AsNoTracking().FirstAsync(m => m.Id == message.Id, ct);
        row.Body.ShouldBeNull();
        row.Payload.ShouldContain("\"foo\"");
        row.Payload.ShouldContain("bar");
    }

    private async Task<(EfMessageWriter Writer, SpringDbContext Db, string ThreadId, DateTimeOffset Baseline)> SetupAsync(
        Guid tenantId,
        Address senderAddress,
        Address recipientAddress,
        CancellationToken cancellationToken,
        IParticipantDisplayNameResolver? resolver = null)
    {
        var tenantContext = new StaticTenantContext(tenantId);
        var db = new SpringDbContext(_dbOptions, tenantContext);

        // Allocate a thread row through the registry so the FK insert in
        // EfMessageWriter has a valid principal.
        var registry = new EfThreadRegistry(db, tenantContext);
        var threadId = await registry.GetOrCreateAsync(new[] { senderAddress, recipientAddress }, cancellationToken);

        var threadGuid = ParseGuid(threadId);
        var baseline = await db.Threads.AsNoTracking()
            .Where(t => t.Id == threadGuid)
            .Select(t => t.LastActivityAt)
            .FirstAsync(cancellationToken);

        // Default resolver returns a fallback for every address so existing
        // tests that don't care about the snapshot path observe empty
        // snapshots — the writer never overwrites a real name with a
        // fallback, so this is a clean baseline.
        resolver ??= new FallbackOnlyParticipantDisplayNameResolver();
        var writer = new EfMessageWriter(db, tenantContext, resolver, BuildPayloadRenderers(), NullLoggerFactory.Instance);
        return (writer, db, threadId, baseline);
    }

    /// <summary>
    /// Test-double resolver that always reports a per-scheme fallback so
    /// the writer never captures a snapshot. The snapshot-path tests
    /// substitute a stub that returns real names for the addresses they
    /// care about.
    /// </summary>
    private sealed class FallbackOnlyParticipantDisplayNameResolver : IParticipantDisplayNameResolver
    {
        public ValueTask<string> ResolveAsync(string address, CancellationToken cancellationToken = default)
            => ValueTask.FromResult("an actor");

        public ValueTask<ParticipantDisplayName> ResolveStatusAsync(string address, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ParticipantDisplayName("an actor", IsFallback: true));

        public ValueTask<bool> IsDeletedAsync(string address, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    /// <summary>
    /// Test-double resolver that returns a configured real name for
    /// known addresses (everything else falls back). Used by the
    /// snapshot-capture tests below.
    /// </summary>
    private sealed class FixedNameParticipantDisplayNameResolver(IReadOnlyDictionary<string, string> names) : IParticipantDisplayNameResolver
    {
        public ValueTask<string> ResolveAsync(string address, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ResolveStatusAsync(address, cancellationToken).Result.DisplayName);

        public ValueTask<ParticipantDisplayName> ResolveStatusAsync(string address, CancellationToken cancellationToken = default)
            => names.TryGetValue(address, out var name)
                ? ValueTask.FromResult(new ParticipantDisplayName(name, IsFallback: false))
                : ValueTask.FromResult(new ParticipantDisplayName("a fallback", IsFallback: true));

        public ValueTask<bool> IsDeletedAsync(string address, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private static Message NewDomainMessage(
        Address from,
        Address to,
        string threadId,
        JsonElement payload,
        DateTimeOffset? sentAt = null) =>
        new(
            Guid.NewGuid(),
            from,
            to,
            MessageType.Domain,
            threadId,
            payload,
            sentAt ?? DateTimeOffset.UtcNow);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    // Local convenience: the production helper exposes TryParse only; tests
    // deal in known-valid thread ids and want a one-shot parse.
    private static Guid ParseGuid(string value)
    {
        if (!GuidFormatter.TryParse(value, out var parsed))
        {
            throw new FormatException($"Value '{value}' is not a Guid.");
        }

        return parsed;
    }

    // #2843: real registry with the platform's built-in renderer set so the
    // existing body-extraction assertions ("Output" → row.Body) exercise
    // the canonical path instead of the old inline heuristic.
    private static IMessagePayloadRendererRegistry BuildPayloadRenderers() =>
        new MessagePayloadRendererRegistry(new IMessagePayloadRenderer[]
        {
            new BareStringPayloadRenderer(),
            new TextPropertyPayloadRenderer(),
            new BodyPropertyPayloadRenderer(),
            new OutputPropertyPayloadRenderer(),
            new ContentPropertyPayloadRenderer(),
        });
}
