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
    /// Creates an authenticated <see cref="IGitHubClient"/> for making API calls
    /// using the connector's default (global) installation id. Issue #2385 made
    /// the per-binding overload the canonical path for unit-owned platform work;
    /// this parameterless overload is now the fallback for connector-level admin
    /// flows (credential validation, install URL, etc.) where no unit binding
    /// is in play. Throws when no global
    /// <see cref="Auth.GitHubConnectorOptions.InstallationId"/> is configured.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An authenticated GitHub client.</returns>
    Task<IGitHubClient> CreateAuthenticatedClientAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an authenticated <see cref="IGitHubClient"/> for the specified
    /// GitHub App installation. Platform-owned connector work that targets a
    /// specific unit binding (webhook registrar, label roundtrip, PR-file
    /// fetcher) MUST call this overload with the binding's
    /// <see cref="UnitGitHubConfig.AppInstallationId"/> so the resulting client
    /// authenticates against the right installation rather than the global
    /// <see cref="Auth.GitHubConnectorOptions.InstallationId"/>. Issue #2385.
    /// </summary>
    /// <param name="installationId">The GitHub App installation id to authenticate as.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An authenticated GitHub client scoped to the given installation.</returns>
    Task<IGitHubClient> CreateAuthenticatedClientAsync(
        long installationId,
        CancellationToken cancellationToken = default);
}
