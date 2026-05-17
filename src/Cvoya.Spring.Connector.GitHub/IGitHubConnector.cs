// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using Cvoya.Spring.Connector.GitHub.Webhooks;

using Octokit;

/// <summary>
/// High-level GitHub connector contract: webhook intake plus authenticated
/// API client creation. Extracted so callers (webhook endpoint, skills) and
/// tests can substitute an alternative implementation without Octokit.
/// </summary>
public interface IGitHubConnector
{
    /// <summary>
    /// Gets the webhook handler for processing inbound GitHub events.
    /// </summary>
    IGitHubWebhookHandler WebhookHandler { get; }

    /// <summary>
    /// Processes an incoming webhook payload, validates its signature,
    /// translates the event into a domain message, and applies any
    /// per-binding inbound filter (#2407). A filter-drop surfaces as
    /// <see cref="WebhookOutcome.Ignored"/>, identical to the
    /// "event-type not handled" path.
    /// </summary>
    /// <param name="eventType">The GitHub event type from the X-GitHub-Event header.</param>
    /// <param name="payload">The raw webhook payload body.</param>
    /// <param name="signature">The signature from the X-Hub-Signature-256 header.</param>
    /// <param name="cancellationToken">Cancellation propagated from the request.</param>
    /// <returns>A <see cref="WebhookHandleResult"/> describing the outcome.</returns>
    Task<WebhookHandleResult> HandleWebhookAsync(
        string eventType,
        string payload,
        string signature,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an authenticated <see cref="IGitHubClient"/> for making API calls.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An authenticated GitHub client.</returns>
    Task<IGitHubClient> CreateAuthenticatedClientAsync(CancellationToken cancellationToken = default);
}
