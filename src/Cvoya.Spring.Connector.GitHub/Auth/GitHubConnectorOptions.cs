/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

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
}
