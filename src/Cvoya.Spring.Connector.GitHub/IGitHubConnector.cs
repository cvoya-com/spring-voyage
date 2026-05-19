// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using Cvoya.Spring.Connector.GitHub.Webhooks;

using Octokit;

/// <summary>
/// High-level GitHub connector contract: webhook intake plus binding-driven
/// authenticated API client creation. Extracted so callers (webhook
/// endpoint, label roundtrip, PR-files fetcher) and tests can substitute an
/// alternative implementation without Octokit.
/// </summary>
/// <remarks>
/// Per ADR-0047 §6 every outbound GitHub call originates from a unit
/// binding and dispatches on the single auth field that is set
/// (<see cref="UnitGitHubConfig.AppInstallationId"/> or
/// <see cref="UnitGitHubConfig.PatSecretName"/>). The contract exposes one
/// client factory keyed on the binding payload; there is no parameterless
/// "global default" or naked-installation-id overload — the resolver
/// (<see cref="Auth.GitHubBindingAuthResolver"/>) is the single dispatch
/// point.
/// </remarks>
public interface IGitHubConnector
{
    /// <summary>
    /// Gets the webhook handler for processing inbound GitHub events.
    /// </summary>
    IGitHubWebhookHandler WebhookHandler { get; }

    /// <summary>
    /// Processes an incoming webhook payload, validates its signature,
    /// translates the event into one or more domain messages (one per
    /// matching binding within the receiving tenant per ADR-0047 §10), and
    /// applies each binding's per-binding inbound filter (#2407). A
    /// filter-drop on every binding surfaces as
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
    /// Creates an authenticated <see cref="IGitHubClient"/> for the
    /// supplied binding per ADR-0047 §6. The connector resolves the
    /// binding's pinned credential through
    /// <see cref="Auth.GitHubBindingAuthResolver"/> — App-installation
    /// token mint when <see cref="UnitGitHubConfig.AppInstallationId"/> is
    /// set, tenant-secret-store PAT read when
    /// <see cref="UnitGitHubConfig.PatSecretName"/> is set — and wires the
    /// result into Octokit. Raises
    /// <see cref="Auth.GitHubBindingAuthMissingException"/> when the
    /// binding's pinned credential cannot be materialised at use time
    /// (secret missing, installation token mint rejected, or the binding-
    /// create gate's invariant was bypassed).
    /// </summary>
    /// <param name="binding">The unit's GitHub binding payload.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An authenticated GitHub client scoped to the binding.</returns>
    Task<IGitHubClient> CreateAuthenticatedClientForBindingAsync(
        UnitGitHubConfig binding,
        CancellationToken cancellationToken = default);
}
