// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents a conversation channel within an agent's partitioned mailbox.
/// Each channel holds messages for a single conversation, identified by <see cref="ConversationId"/>.
/// </summary>
public class ConversationChannel
{
    /// <summary>
    /// Gets the unique identifier for this conversation.
    /// </summary>
    public string ConversationId { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the queue of messages awaiting processing in this conversation.
    /// </summary>
    public List<Message> Messages { get; set; } = [];

    /// <summary>
    /// Gets or sets the timestamp when this conversation was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
