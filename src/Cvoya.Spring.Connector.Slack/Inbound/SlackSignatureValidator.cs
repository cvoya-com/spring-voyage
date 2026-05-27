// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Inbound;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Default <see cref="ISlackSignatureValidator"/>. Implements the
/// signature-verification scheme Slack publishes at
/// <see href="https://api.slack.com/authentication/verifying-requests-from-slack"/>.
/// </summary>
public sealed class SlackSignatureValidator : ISlackSignatureValidator
{
    /// <summary>
    /// Maximum allowed clock skew between Slack's request timestamp
    /// and the local clock. Slack's docs recommend 5 minutes.
    /// </summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    private const string SignaturePrefix = "v0=";
    private const string BaseStringPrefix = "v0:";

    /// <inheritdoc />
    public bool Validate(
        string rawBody,
        string? timestamp,
        string? signature,
        string signingSecret,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(rawBody)
            || string.IsNullOrEmpty(timestamp)
            || string.IsNullOrEmpty(signature)
            || string.IsNullOrEmpty(signingSecret))
        {
            return false;
        }

        // Timestamp must be a base-10 long.
        if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return false;
        }

        var nowTime = now ?? DateTimeOffset.UtcNow;
        var requestTime = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var skew = (nowTime - requestTime).Duration();
        if (skew > MaxSkew)
        {
            return false;
        }

        // Signature must carry the "v0=" prefix.
        if (!signature.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var providedHex = signature[SignaturePrefix.Length..];

        // Compute HMAC-SHA256 over the v0 base string.
        var baseString = $"{BaseStringPrefix}{timestamp}:{rawBody}";
        var keyBytes = Encoding.UTF8.GetBytes(signingSecret);
        var bytes = Encoding.UTF8.GetBytes(baseString);
        var hash = HMACSHA256.HashData(keyBytes, bytes);
        var computedHex = Convert.ToHexStringLower(hash);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHex),
            Encoding.UTF8.GetBytes(providedHex));
    }
}
