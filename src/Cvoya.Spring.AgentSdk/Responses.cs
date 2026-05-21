// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Text.Json.Serialization;

// ADR-0049 — message-delivery tools are RPCs whose response is a delivery
// acknowledgement: the message was durably placed in the recipient's
// mailbox. They never carry the recipient's work product.

/// <summary>Delivery acknowledgement for a <c>sv.messaging.send</c> call.</summary>
public record MessageSendResponse(
    [property: JsonPropertyName("delivered")] bool Delivered,
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("threadId")] string ThreadId);

/// <summary>Per-target delivery outcomes for a <c>sv.messaging.multicast</c> call.</summary>
public record MessageMulticastResponse(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("threadId")] string ThreadId,
    [property: JsonPropertyName("deliveries")] IReadOnlyList<MessageMulticastDelivery> Deliveries);

/// <summary>Delivery outcome for a single <c>sv.messaging.multicast</c> target.</summary>
public record MessageMulticastDelivery(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("delivered")] bool Delivered,
    [property: JsonPropertyName("error")] string? Error);
