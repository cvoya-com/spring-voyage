// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Text.Json.Serialization;

// ADR-0049 — message-delivery tools are RPCs whose response is a delivery
// acknowledgement: the message was durably placed in the recipient's
// mailbox. They never carry the recipient's work product.

/// <summary>Delivery acknowledgement for a <c>delegate_to</c> call.</summary>
public record DelegateResponse(
    [property: JsonPropertyName("delivered")] bool Delivered,
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("threadId")] string ThreadId);

/// <summary>Per-target delivery outcomes for a <c>fanout_to</c> call.</summary>
public record FanoutResponse(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("threadId")] string ThreadId,
    [property: JsonPropertyName("deliveries")] IReadOnlyList<FanoutDelivery> Deliveries);

/// <summary>Delivery outcome for a single <c>fanout_to</c> target.</summary>
public record FanoutDelivery(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("delivered")] bool Delivered,
    [property: JsonPropertyName("error")] string? Error);
