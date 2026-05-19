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
    /// Translates a GitHub webhook event into one domain message per
    /// matching unit binding in the receiving tenant per ADR-0047 §10.
    /// The matcher keys on the payload's <c>(owner, repo)</c> within the
    /// tenant; many bindings per <c>(tenant, owner, repo)</c> is supported.
    /// </summary>
    /// <param name="eventType">The GitHub event type from the X-GitHub-Event header.</param>
    /// <param name="payload">The parsed JSON payload.</param>
    /// <param name="cancellationToken">A token to cancel the binding lookup.</param>
    /// <returns>
    /// One unit-addressed domain <see cref="Message"/> per matching
    /// binding. Returns an empty list when the event type is not handled
    /// or no binding in the receiving tenant matches the inbound payload's
    /// <c>(owner, repo)</c>. The connector treats both shapes identically
    /// — silent drop, ACK 202.
    /// </returns>
    Task<IReadOnlyList<Message>> TranslateEventAsync(
        string eventType, JsonElement payload, CancellationToken cancellationToken = default);

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
}
