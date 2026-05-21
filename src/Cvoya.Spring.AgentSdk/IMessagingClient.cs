// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

/// <summary>
/// Runtime-author-facing client for the Spring Voyage messaging callbacks.
/// The platform delivers messages; it does not orchestrate (ADR-0048 /
/// ADR-0049).
/// </summary>
public interface IMessagingClient
{
    /// <summary>Posts a result back to the dispatcher thread.</summary>
    Task PostResultAsync(string threadId, string result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers a message to a single target. ADR-0049 — the returned
    /// <see cref="MessageSendResponse"/> is a delivery acknowledgement (the
    /// message reached the target's mailbox), never the target's response.
    /// </summary>
    Task<MessageSendResponse> SendAsync(
        string threadId,
        string targetUnitId,
        string prompt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers a message to multiple targets. ADR-0049 — the returned
    /// <see cref="MessageMulticastResponse"/> reports per-target delivery
    /// outcomes, not the targets' work products.
    /// </summary>
    Task<MessageMulticastResponse> MulticastAsync(
        string threadId,
        IReadOnlyList<string> targetUnitIds,
        string prompt,
        CancellationToken cancellationToken = default);
}
