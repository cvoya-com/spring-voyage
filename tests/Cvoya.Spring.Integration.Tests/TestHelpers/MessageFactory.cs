// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests.TestHelpers;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Factory for creating test messages with sensible defaults.
/// Post #1629: every address path is a Guid; the no-id overloads now take
/// either a Guid or a no-dash hex string that <see cref="Address.For"/>
/// can parse.
/// </summary>
public static class MessageFactory
{
    private static readonly Guid DefaultSenderId = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid DefaultReceiverId = new("aaaaaaaa-1111-1111-1111-000000000002");
    private static readonly Guid DefaultUnitId = new("bbbbbbbb-1111-1111-1111-000000000001");
    private static readonly Guid DefaultConnectorId = new("cccccccc-1111-1111-1111-000000000001");

    private static Address ParseOrDefault(string scheme, string? id, Guid fallback) =>
        id is null ? new Address(scheme, fallback) : Address.For(scheme, ToHex(id));

    /// <summary>
    /// Treat the caller's id as either a literal Guid (canonical or dashed)
    /// or as a stable test slug, mapped through <see cref="TestSlugIds"/>
    /// so the test's referential identity is preserved across calls.
    /// </summary>
    private static string ToHex(string id)
    {
        return Guid.TryParse(id, out _) ? id : TestSlugIds.HexFor(id);
    }

    /// <summary>
    /// Creates a domain message with optional overrides.
    /// </summary>
    public static Message CreateDomainMessage(
        string? threadId = null,
        string? fromId = null,
        string? toId = null,
        string fromType = "agent",
        string toType = "agent",
        JsonElement? payload = null)
    {
        return new Message(
            Guid.NewGuid(),
            ParseOrDefault(fromType, fromId, DefaultSenderId),
            ParseOrDefault(toType, toId, DefaultReceiverId),
            MessageType.Domain,
            threadId ?? Guid.NewGuid().ToString(),
            payload ?? JsonSerializer.SerializeToElement(new { Content = "test-payload" }),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Creates a status query message.
    /// </summary>
    public static Message CreateStatusQuery(string fromId, string toId, string toType = "agent")
    {
        return new Message(
            Guid.NewGuid(),
            Address.For("agent", ToHex(fromId)),
            Address.For(toType, ToHex(toId)),
            MessageType.StatusQuery,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { }),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Creates a cancel message for a specific thread.
    /// </summary>
    public static Message CreateCancelMessage(string threadId, string fromId, string toId, string toType = "agent")
    {
        return new Message(
            Guid.NewGuid(),
            Address.For("agent", ToHex(fromId)),
            Address.For(toType, ToHex(toId)),
            MessageType.Cancel,
            threadId,
            JsonSerializer.SerializeToElement(new { }),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Creates a domain message simulating a GitHub webhook payload.
    /// </summary>
    public static Message CreateWebhookMessage(
        string? threadId = null,
        string? toId = null,
        string toType = "unit")
    {
        var webhookPayload = JsonSerializer.SerializeToElement(new
        {
            EventType = "issues",
            Action = "opened",
            Repository = "test-org/test-repo",
            Issue = new
            {
                Number = 42,
                Title = "Fix the widget",
                Body = "The widget is broken and needs fixing."
            }
        });

        return new Message(
            Guid.NewGuid(),
            new Address("connector", DefaultConnectorId),
            ParseOrDefault(toType, toId, DefaultUnitId),
            MessageType.Domain,
            threadId ?? Guid.NewGuid().ToString(),
            webhookPayload,
            DateTimeOffset.UtcNow);
    }
}