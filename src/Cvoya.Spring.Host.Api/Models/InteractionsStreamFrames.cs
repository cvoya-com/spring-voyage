// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// SSE frame types emitted by
/// <c>GET /api/v1/tenant/observation/interactions/stream</c> (#2867). The
/// payload travels as the data section of an SSE <c>event:</c> frame; the
/// event-name header identifies which DTO is on the wire. Kept as
/// per-frame records so the OpenAPI / portal contract is explicit about
/// the four shapes — pulse, node-added, edge-added, throttled.
/// </summary>
public static class InteractionsStreamEvents
{
    /// <summary>SSE event name for <see cref="InteractionsPulseFrame"/>.</summary>
    public const string Pulse = "pulse";

    /// <summary>SSE event name for <see cref="InteractionsNodeAddedFrame"/>.</summary>
    public const string NodeAdded = "node-added";

    /// <summary>SSE event name for <see cref="InteractionsEdgeAddedFrame"/>.</summary>
    public const string EdgeAdded = "edge-added";

    /// <summary>SSE event name for <see cref="InteractionsThrottledFrame"/>.</summary>
    public const string Throttled = "throttled";
}

/// <summary>
/// Pulse frame — emitted after the per-edge coalesce window closes
/// (250 ms default). Carries every message id observed for that edge
/// within the window so the portal can light up a single animation while
/// still surfacing the underlying volume.
/// </summary>
/// <param name="MessageIds">
/// Canonical no-dash 32-hex Guids of the messages coalesced into this
/// pulse. At least one entry; <see cref="Count"/> matches the length.
/// </param>
/// <param name="FromId">Canonical Guid of the sender.</param>
/// <param name="ToId">Canonical Guid of the recipient.</param>
/// <param name="Timestamp">Timestamp of the most recent message in the coalesce window.</param>
/// <param name="ThreadId">Thread id of the most recent message (null when not parseable).</param>
/// <param name="Channel">Recipient scheme of the most recent message in the window.</param>
/// <param name="Count">Number of messages coalesced into this pulse.</param>
public record InteractionsPulseFrame(
    IReadOnlyList<string> MessageIds,
    string FromId,
    string ToId,
    DateTimeOffset Timestamp,
    string? ThreadId,
    string Channel,
    long Count);

/// <summary>
/// Frame emitted immediately before the first <see cref="InteractionsPulseFrame"/>
/// that references a previously-unseen node. Tells the visualization to
/// allocate the node ahead of the animation that touches it.
/// </summary>
/// <param name="Id">Canonical no-dash 32-hex Guid of the new node.</param>
/// <param name="Kind">Address scheme: <c>agent</c>, <c>unit</c>, <c>human</c>, or <c>connector</c>.</param>
/// <param name="DisplayName">Resolved live display name; falls back to a per-scheme generic.</param>
public record InteractionsNodeAddedFrame(
    string Id,
    string Kind,
    string DisplayName);

/// <summary>
/// Frame emitted immediately before the first <see cref="InteractionsPulseFrame"/>
/// that references a previously-unseen <c>(fromId, toId)</c> pair.
/// </summary>
/// <param name="FromId">Canonical Guid of the sender.</param>
/// <param name="ToId">Canonical Guid of the recipient.</param>
public record InteractionsEdgeAddedFrame(
    string FromId,
    string ToId);

/// <summary>
/// Frame emitted when the per-subscription rate cap fires (default
/// 50 events/second). Drops the burst rather than backpressuring the
/// activity bus; the portal renders an "events dropped" indicator.
/// </summary>
/// <param name="Since">UTC timestamp of the most recent dropped message in this window.</param>
/// <param name="Dropped">Number of messages dropped since the previous throttled frame (or since stream start).</param>
public record InteractionsThrottledFrame(
    DateTimeOffset Since,
    long Dropped);
