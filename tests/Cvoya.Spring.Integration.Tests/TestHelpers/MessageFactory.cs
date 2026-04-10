// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests.TestHelpers;

using System.Text.Json;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Factory for creating test messages with sensible defaults.
/// </summary>
public static class MessageFactory
{
    /// <summary>
    /// Creates a domain message with optional overrides.
    /// </summary>
    public static Message CreateDomainMessage(
        string? conversationId = null,
        string? fromId = null,
        string? toId = null,
        string fromType = "agent",
        string toType = "agent",
        JsonElement? payload = null)
    {
        return new Message(
            Guid.NewGuid(),
            new Address(fromType, fromId ?? "test-sender"),
            new Address(toType, toId ?? "test-receiver"),
            MessageType.Domain,
            conversationId ?? Guid.NewGuid().ToString(),
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
            new Address("agent", fromId),
            new Address(toType, toId),
            MessageType.StatusQuery,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { }),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Creates a cancel message for a specific conversation.
    /// </summary>
    public static Message CreateCancelMessage(string conversationId, string fromId, string toId, string toType = "agent")
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", fromId),
            new Address(toType, toId),
            MessageType.Cancel,
            conversationId,
            JsonSerializer.SerializeToElement(new { }),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Creates a domain message simulating a GitHub webhook payload.
    /// </summary>
    public static Message CreateWebhookMessage(
        string? conversationId = null,
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
            new Address("connector", "github-connector"),
            new Address(toType, toId ?? "test-unit"),
            MessageType.Domain,
            conversationId ?? Guid.NewGuid().ToString(),
            webhookPayload,
            DateTimeOffset.UtcNow);
    }
}
