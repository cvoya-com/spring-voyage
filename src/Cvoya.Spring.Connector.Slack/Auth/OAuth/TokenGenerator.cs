// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Auth.OAuth;

using System.Security.Cryptography;

/// <summary>
/// Cryptographic random-string helper used by the Slack OAuth
/// service to mint state tokens. Mirrors the GitHub connector's
/// generator (same bit width, same URL-safe base64 transform).
/// </summary>
internal static class TokenGenerator
{
    /// <summary>
    /// Returns a URL-safe base64 string seeded by
    /// <paramref name="bytes"/> bytes of cryptographic randomness.
    /// </summary>
    public static string UrlSafe(int bytes)
    {
        if (bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        Span<byte> buffer = bytes <= 64 ? stackalloc byte[bytes] : new byte[bytes];
        RandomNumberGenerator.Fill(buffer);

        return Convert.ToBase64String(buffer)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
