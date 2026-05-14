// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="MessageQueryService"/> — the EF-authoritative
/// read path that returns a single message's body / envelope / payload from
/// the <c>messages</c> table (#1209 / #2054). Pre-rewrite this scanned the
/// activity-event JSON; the legacy seed shape is gone.
/// </summary>
public class MessageQueryServiceTests : IDisposable
{
    private static readonly Guid TenantId = new("aaaa1001-2222-3333-4444-000000000001");
    private static readonly Guid OtherTenantId = new("aaaa1001-2222-3333-4444-000000000002");

    private readonly DbContextOptions<SpringDbContext> _dbOptions;
    private readonly SpringDbContext _db;
    private readonly ITenantContext _tenantContext;

    public MessageQueryServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase($"MessageQueryTest-{Guid.NewGuid()}")
            .Options;
        _tenantContext = new StaticTenantContext(TenantId);
        _db = new SpringDbContext(_dbOptions, _tenantContext);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetAsync_NoRow_ReturnsNull()
    {
        var svc = new MessageQueryService(_db);

        var result = await svc.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_EmptyGuid_ReturnsNull()
    {
        var svc = new MessageQueryService(_db);

        var result = await svc.GetAsync(Guid.Empty, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_KnownId_ReturnsEnvelopeAndBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var savaspId = Guid.NewGuid();
        var adaId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var sentAt = DateTimeOffset.UtcNow;

        SeedThread(threadId);
        SeedMessage(messageId, threadId, "human", savaspId, "agent", adaId, "hello, ada", "\"hello, ada\"", sentAt);
        await _db.SaveChangesAsync(ct);

        var svc = new MessageQueryService(_db);
        var result = await svc.GetAsync(messageId, ct);

        result.ShouldNotBeNull();
        result!.MessageId.ShouldBe(messageId);
        result.ThreadId.ShouldBe(GuidFormatter.Format(threadId));
        result.From.ShouldBe($"human://{GuidFormatter.Format(savaspId)}");
        result.To.ShouldBe($"agent://{GuidFormatter.Format(adaId)}");
        result.MessageType.ShouldBe("Domain");
        result.Body.ShouldBe("hello, ada");
        result.Timestamp.ShouldBe(sentAt);
    }

    [Fact]
    public async Task GetAsync_StructuredPayload_LeavesBodyNullAndPreservesPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var graceId = Guid.NewGuid();
        var adaId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { kind = "amend", text = "hi" });

        SeedThread(threadId);
        SeedMessage(messageId, threadId, "agent", graceId, "agent", adaId, body: null, payload, DateTimeOffset.UtcNow);
        await _db.SaveChangesAsync(ct);

        var svc = new MessageQueryService(_db);
        var result = await svc.GetAsync(messageId, ct);

        result.ShouldNotBeNull();
        result!.Body.ShouldBeNull();
        result.Payload.ShouldNotBeNull();
        result.Payload!.Value.GetProperty("kind").GetString().ShouldBe("amend");
    }

    [Fact]
    public async Task GetAsync_DifferentMessageId_DoesNotLeak()
    {
        var ct = TestContext.Current.CancellationToken;
        var seededId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        SeedThread(threadId);
        SeedMessage(seededId, threadId, "human", Guid.NewGuid(), "agent", Guid.NewGuid(), "hi", "\"hi\"", DateTimeOffset.UtcNow);
        await _db.SaveChangesAsync(ct);

        var svc = new MessageQueryService(_db);
        var result = await svc.GetAsync(Guid.NewGuid(), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_TenantIsolated_OtherTenantDoesNotSeeRow()
    {
        // The DbContext-level query filter on MessageEntity scopes reads to
        // CurrentTenantId. A second context bound to OtherTenantId must not
        // surface the seeded row even though the in-memory store carries it.
        var ct = TestContext.Current.CancellationToken;
        var messageId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        SeedThread(threadId);
        SeedMessage(messageId, threadId, "human", Guid.NewGuid(), "agent", Guid.NewGuid(), "secret", "\"secret\"", DateTimeOffset.UtcNow);
        await _db.SaveChangesAsync(ct);

        using var otherDb = new SpringDbContext(_dbOptions, new StaticTenantContext(OtherTenantId));
        var svc = new MessageQueryService(otherDb);
        var result = await svc.GetAsync(messageId, ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_RetractedMessage_StillResolvesWithRetractedAtStamped()
    {
        // The retraction issue only ships the column today (#2053 reserves
        // it). Verify the read path doesn't filter retracted rows out — the
        // surface decides whether to render the strikethrough; the query
        // service stays neutral.
        var ct = TestContext.Current.CancellationToken;
        var messageId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        SeedThread(threadId);
        var entity = SeedMessage(messageId, threadId, "agent", Guid.NewGuid(), "human", Guid.NewGuid(), "wrong reply", "\"wrong reply\"", DateTimeOffset.UtcNow);
        entity.RetractedAt = DateTimeOffset.UtcNow.AddSeconds(5);
        await _db.SaveChangesAsync(ct);

        var svc = new MessageQueryService(_db);
        var result = await svc.GetAsync(messageId, ct);

        result.ShouldNotBeNull();
        result!.Body.ShouldBe("wrong reply");
    }

    private void SeedThread(Guid threadId)
    {
        _db.Threads.Add(new ThreadEntity
        {
            Id = threadId,
            TenantId = TenantId,
            ParticipantKey = $"thread-{threadId:N}",
            Participants = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        });
    }

    private MessageEntity SeedMessage(
        Guid id,
        Guid threadId,
        string senderScheme,
        Guid senderId,
        string recipientScheme,
        Guid recipientId,
        string? body,
        string payload,
        DateTimeOffset sentAt)
    {
        var entity = new MessageEntity
        {
            Id = id,
            TenantId = TenantId,
            ThreadId = threadId,
            SenderScheme = senderScheme,
            SenderId = senderId,
            RecipientScheme = recipientScheme,
            RecipientId = recipientId,
            MessageType = nameof(MessageType.Domain),
            Body = body,
            Payload = payload,
            SentAt = sentAt,
            RetractedAt = null,
        };
        _db.Messages.Add(entity);
        return entity;
    }
}
