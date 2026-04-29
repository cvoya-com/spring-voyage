// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents a thread channel within an agent's partitioned mailbox.
/// Each channel holds messages for a single thread, identified by <see cref="ThreadId"/>.
/// </summary>
public class ThreadChannel
{
    /// <summary>
    /// Gets the unique identifier for this thread.
    /// </summary>
    public string ThreadId { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the queue of messages awaiting processing in this thread.
    /// </summary>
    public List<Message> Messages { get; set; } = [];

    /// <summary>
    /// Gets or sets the timestamp when this thread was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}