// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

/// <summary>
/// Configuration options for the GitHub connector, including GitHub App
/// credentials and webhook secret.
/// </summary>
public class GitHubConnectorOptions
{
    /// <summary>
    /// Gets or sets the GitHub App ID.
    /// </summary>
    public long AppId { get; set; }

    /// <summary>
    /// Gets or sets the PEM-encoded private key for the GitHub App.
    /// </summary>
    public string PrivateKeyPem { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the webhook secret used to validate incoming webhook payloads.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Optional default installation id used by the connector when no
    /// per-binding installation is available. Issue #2385 made
    /// <see cref="UnitGitHubConfig.AppInstallationId"/> the canonical auth
    /// path for unit-owned platform work (webhook registrar, label
    /// roundtrip, PR-files fetcher) — this option is now reserved for:
    /// <list type="bullet">
    ///   <item><description>Connector-level admin flows (credential validation, install URL, the OAuth surface).</description></item>
    ///   <item><description>OSS single-installation deployments that never bound a per-unit installation id.</description></item>
    /// </list>
    /// Deployments with more than one App installation MUST bind a per-unit
    /// installation id; the global fallback will select an arbitrary
    /// installation and silently mis-route credentials.
    /// </summary>
    public long? InstallationId { get; set; }

    // Issue #2456 removed:
    //   * DefaultTargetUnitPath — the App-level delivery path looks up
    //     the target unit by matching the inbound payload's
    //     (installation_id, owner, repo) against per-unit bindings; there
    //     is no single configured fallback unit any more.
    //   * WebhookUrl — the GitHub App owns the webhook URL (registered
    //     once by the operator when the App is created). The platform no
    //     longer creates per-repo hooks, so there is no caller for this
    //     setting either.

    // (Both fields removed in #2456 — no shim, no opt-in flag.)

    /// <summary>
    /// Gets or sets the GitHub App slug used when constructing the install URL
    /// (e.g. <c>https://github.com/apps/{AppSlug}/installations/new</c>). This
    /// is the App's public slug as shown in its GitHub settings page, not the
    /// numeric <see cref="AppId"/>. When unset, the
    /// <c>integrations/github/install-url</c> endpoint returns 502 because the
    /// install URL cannot be constructed.
    /// </summary>
    public string AppSlug { get; set; } = string.Empty;
}
