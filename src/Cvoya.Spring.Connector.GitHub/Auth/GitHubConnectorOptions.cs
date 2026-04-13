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
    /// Gets or sets the optional installation ID to use for authentication.
    /// When set, the connector authenticates as this specific installation.
    /// </summary>
    public long? InstallationId { get; set; }

    /// <summary>
    /// Gets or sets the unit address path (e.g. "engineering-team") to which
    /// webhook-translated messages should be delivered. Until a proper
    /// installation-id → unit mapping lands, this single configured path is
    /// used as the destination for every webhook the connector translates.
    /// When left empty the handler falls back to the legacy <c>system://router</c>
    /// address, which <see cref="Cvoya.Spring.Core.Messaging.IMessageRouter"/>
    /// does not recognize — produced messages will not be delivered.
    /// </summary>
    public string DefaultTargetUnitPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the publicly reachable URL that GitHub should deliver
    /// webhooks to (e.g. <c>https://example.com/api/v1/webhooks/github</c>).
    /// Consumed by <see cref="Webhooks.IGitHubWebhookRegistrar"/> when a unit
    /// starts so that freshly registered hooks point back at this platform.
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

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