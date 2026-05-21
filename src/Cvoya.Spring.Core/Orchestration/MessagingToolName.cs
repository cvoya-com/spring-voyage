// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

using System.Text.Json.Serialization;

/// <summary>
/// Canonical names for the platform messaging tools an agent or unit may
/// invoke to deliver messages to any addressable target. Wire form is the
/// dotted <c>sv.messaging.*</c> taxonomy (preserved via
/// <see cref="JsonStringEnumMemberNameAttribute"/>) so manifests, logs,
/// and runtime-side tool catalogues use a single name.
/// </summary>
/// <remarks>
/// The messaging surface is the two delivery verbs only — discovery,
/// inspection, and status queries live on the <c>sv.directory.*</c> tool
/// surface. The platform delivers messages; it does not orchestrate
/// (ADR-0048 / ADR-0049): <c>sv.messaging.send</c> delivers to one target,
/// <c>sv.messaging.broadcast</c> delivers to many. The response is a
/// delivery acknowledgement, never the recipient's reply.
/// </remarks>
public enum MessagingToolName
{
    /// <summary>Deliver a message to a single addressable target.</summary>
    [JsonStringEnumMemberName("sv.messaging.send")]
    Send,

    /// <summary>Deliver a message to multiple addressable targets in parallel.</summary>
    [JsonStringEnumMemberName("sv.messaging.broadcast")]
    Broadcast
}
