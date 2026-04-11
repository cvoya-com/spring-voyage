// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

using System.Text.Json;

/// <summary>
/// Represents an immutable message exchanged between addressable components
/// in the Spring Voyage platform.
/// </summary>
/// <param name="Id">The unique identifier of the message.</param>
/// <param name="From">The address of the message sender.</param>
/// <param name="To">The address of the message recipient.</param>
/// <param name="Type">The type of message.</param>
/// <param name="ConversationId">An optional conversation identifier for correlating related messages.</param>
/// <param name="Payload">The message payload as a JSON element.</param>
/// <param name="Timestamp">The timestamp when the message was created.</param>
public record Message(
    Guid Id,
    Address From,
    Address To,
    MessageType Type,
    string? ConversationId,
    JsonElement Payload,
    DateTimeOffset Timestamp);