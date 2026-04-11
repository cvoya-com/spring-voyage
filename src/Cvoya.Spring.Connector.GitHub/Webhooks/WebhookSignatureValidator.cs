// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Validates GitHub webhook signatures using HMAC-SHA256 to ensure
/// that incoming payloads are authentic.
/// </summary>
public static class WebhookSignatureValidator
{
    /// <summary>
    /// Validates a GitHub webhook payload signature against the expected secret.
    /// </summary>
    /// <param name="payload">The raw webhook payload body.</param>
    /// <param name="signature">The signature from the X-Hub-Signature-256 header (format: "sha256=...").</param>
    /// <param name="secret">The webhook secret configured in the GitHub App.</param>
    /// <returns><c>true</c> if the signature is valid; otherwise, <c>false</c>.</returns>
    public static bool Validate(string payload, string signature, string secret)
    {
        if (string.IsNullOrEmpty(payload) || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        if (!signature.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }

        var expectedHash = signature["sha256=".Length..];

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        var computedHash = Convert.ToHexStringLower(hash);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHash),
            Encoding.UTF8.GetBytes(expectedHash));
    }
}