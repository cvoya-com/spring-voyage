// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Auth;

using System.Security.Cryptography;
using System.Text;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Runtime;

using Microsoft.Extensions.Options;

internal sealed class DispatcherTenantSigningKeyProvider(
    IOptions<DispatcherOptions> options) : ITenantSigningKeyProvider
{
    private readonly DispatcherOptions _options = options.Value;

    public byte[] GetSigningKey(Guid tenantId)
    {
        if (_options.Tokens.Count == 0)
        {
            throw new InvalidOperationException(
                $"Dispatcher:Tokens must contain at least one token to validate callback tokens for tenant " +
                $"'{GuidFormatter.Format(tenantId)}'.");
        }

        foreach (var (token, scope) in _options.Tokens)
        {
            if (GuidFormatter.TryParse(scope.TenantId, out var scopedTenantId) &&
                scopedTenantId == tenantId)
            {
                return DeriveSigningKey(token);
            }
        }

        if (_options.Tokens.Count == 1)
        {
            // Single-token OSS deployments often configure a stable bearer
            // token with a non-Guid tenant label. In that mode the lone token
            // is intentionally the callback signing key for every tenant claim.
            return DeriveSigningKey(_options.Tokens.Keys.Single());
        }

        throw new InvalidOperationException(
            $"No dispatcher token is configured for tenant '{GuidFormatter.Format(tenantId)}'.");
    }

    private static byte[] DeriveSigningKey(string token)
    {
        // Operators can provide a full 32-byte HMAC key as hex. Other token
        // strings are treated as passphrases and hashed to the required key
        // width; short passphrases are weak but acceptable for the v0.1
        // dispatcher-token compatibility path.
        if (token.Length == 64 && TryDecodeHex(token, out var bytes))
        {
            return bytes;
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    private static bool TryDecodeHex(string value, out byte[] bytes)
    {
        bytes = new byte[value.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var hi = FromHex(value[i * 2]);
            var lo = FromHex(value[(i * 2) + 1]);
            if (hi < 0 || lo < 0)
            {
                bytes = Array.Empty<byte>();
                return false;
            }

            bytes[i] = (byte)((hi << 4) | lo);
        }

        return true;
    }

    private static int FromHex(char c) =>
        c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
}
