// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Translates inbound GitHub webhook payloads into domain <see cref="Message"/> objects.
/// Extracted so callers and tests can substitute a mock translator without touching Octokit.
/// </summary>
public interface IGitHubWebhookHandler
{
    /// <summary>
    /// Translates a GitHub webhook event into a domain message.
    /// </summary>
    /// <param name="eventType">The GitHub event type from the X-GitHub-Event header.</param>
    /// <param name="payload">The parsed JSON payload.</param>
    /// <returns>A domain <see cref="Message"/>, or <c>null</c> if the event type is not handled.</returns>
    Message? TranslateEvent(string eventType, JsonElement payload);

    /// <summary>
    /// Applies the per-binding inbound filter declared on the target unit's
    /// <see cref="UnitGitHubConfig"/> (issue #2407). Returns
    /// <paramref name="translated"/> verbatim when the event passes every
    /// configured filter or when the target unit has no GitHub binding (so
    /// legacy installs without a filter behave exactly as before). Returns
    /// <c>null</c> when a filter drops the event — callers MUST treat the
    /// null exactly like the "event-type not handled" no-deliver path so
    /// the webhook endpoint still ACKs 202 to GitHub. On drop, an activity
    /// event is emitted via <see cref="Core.Capabilities.IActivityEventBus"/>
    /// (when one is registered) for operator audit.
    /// </summary>
    /// <param name="translated">The translated domain message.</param>
    /// <param name="eventType">The GitHub event type (carried into the activity audit).</param>
    /// <param name="cancellationToken">Cancellation propagated from the request.</param>
    Task<Message?> ApplyInboundFilterAsync(
        Message translated,
        string eventType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Derives the set of cache tags that the connector should invalidate in
    /// response to <paramref name="eventType"/> + <paramref name="payload"/>.
    /// Returns an empty sequence when the event has no cacheable surface (or
    /// the payload is missing the required identifiers).
    /// </summary>
    /// <param name="eventType">The GitHub event type from the X-GitHub-Event header.</param>
    /// <param name="payload">The parsed JSON payload.</param>
    IReadOnlyList<string> DeriveInvalidationTags(string eventType, JsonElement payload);
}
