// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Observability;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="MessageQueryService"/> — the projection that
/// surfaces a single message's body / envelope from the activity-event
/// stream so the CLI's <c>spring message show</c> and the portal can
/// render the actual text exchanged (#1209).
/// </summary>
public class MessageQueryServiceTests : IDisposable
{
    private readonly SpringDbContext _db;

    public MessageQueryServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase($"MessageQueryTest-{Guid.NewGuid()}")
            .Options;
        _db = new SpringDbContext(dbOptions);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetAsync_NoEvents_ReturnsNull()
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
        var messageId = Guid.NewGuid();
        var conversationId = "conv-1";
        var message = new Message(
            messageId,
            new Address("human", "savasp"),
            new Address("agent", "ada"),
            MessageType.Domain,
            conversationId,
            JsonSerializer.SerializeToElement("hello, ada"),
            DateTimeOffset.UtcNow);

        SeedReceived(message, conversationId);

        var svc = new MessageQueryService(_db);
        var result = await svc.GetAsync(messageId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.MessageId.ShouldBe(messageId);
        result.ConversationId.ShouldBe(conversationId);
        result.From.ShouldBe("human://savasp");
        result.To.ShouldBe("agent://ada");
        result.MessageType.ShouldBe("Domain");
        result.Body.ShouldBe("hello, ada");
    }

    [Fact]
    public async Task GetAsync_StructuredPayload_LeavesBodyNullAndPreservesPayload()
    {
        // Structured (non-string) payloads — e.g. amendments — surface with
        // Body=null so the CLI / portal can fall back to a JSON dump rather
        // than printing nothing.
        var messageId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new { kind = "amend", text = "hi" });
        var message = new Message(
            messageId,
            new Address("agent", "grace"),
            new Address("agent", "ada"),
            MessageType.Domain,
            "conv-2",
            payload,
            DateTimeOffset.UtcNow);

        SeedReceived(message, "conv-2");

        var svc = new MessageQueryService(_db);
        var result = await svc.GetAsync(messageId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Body.ShouldBeNull();
        result.Payload.ShouldNotBeNull();
        result.Payload!.Value.GetProperty("kind").GetString().ShouldBe("amend");
    }

    [Fact]
    public async Task GetAsync_DifferentMessageId_DoesNotLeak()
    {
        var seededId = Guid.NewGuid();
        var message = new Message(
            seededId,
            new Address("human", "savasp"),
            new Address("agent", "ada"),
            MessageType.Domain,
            "conv-3",
            JsonSerializer.SerializeToElement("hi"),
            DateTimeOffset.UtcNow);

        SeedReceived(message, "conv-3");

        var svc = new MessageQueryService(_db);
        var result = await svc.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    private void SeedReceived(Message message, string? correlationId)
    {
        _db.ActivityEvents.Add(new ActivityEventRecord
        {
            Id = Guid.NewGuid(),
            Source = $"{message.To.Scheme}:{message.To.Path}",
            EventType = nameof(ActivityEventType.MessageReceived),
            Severity = "Info",
            Summary = $"Received {message.Type} message {message.Id} from {message.From}",
            Details = MessageReceivedDetails.Build(message),
            CorrelationId = correlationId,
            Timestamp = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }
}