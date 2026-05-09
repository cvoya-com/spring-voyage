// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

/// <summary>
/// Validates GitHub webhook signatures. Injectable so endpoint and connector
/// callers can substitute a test double without re-implementing HMAC-SHA256.
/// </summary>
public interface IWebhookSignatureValidator
{
    /// <summary>
    /// Validates a GitHub webhook payload signature against the expected secret.
    /// </summary>
    /// <param name="payload">The raw webhook payload body.</param>
    /// <param name="signature">The signature from the X-Hub-Signature-256 header (format: "sha256=...").</param>
    /// <param name="secret">The webhook secret configured in the GitHub App.</param>
    /// <returns><c>true</c> if the signature is valid; otherwise, <c>false</c>.</returns>
    bool Validate(string payload, string signature, string secret);
}
