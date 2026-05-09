// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Security.Cryptography;
using System.Text;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Runtime;

using Microsoft.Extensions.Options;

/// <summary>
/// OSS worker-side callback-token signing key provider backed by the
/// configured dispatcher bearer token.
/// </summary>
/// <remarks>
/// The host script generates a 256-bit hex dispatcher token and shares it
/// with the worker through <c>Dispatcher:BearerToken</c>. The cloud host can
/// replace this registration with a tenant-scoped KMS implementation before
/// calling <c>AddCvoyaSpringDapr()</c>.
/// </remarks>
internal sealed class DispatcherClientTenantSigningKeyProvider(
    IOptions<DispatcherClientOptions> options) : ITenantSigningKeyProvider
{
    private readonly DispatcherClientOptions _options = options.Value;

    public byte[] GetSigningKey(Guid tenantId)
    {
        if (string.IsNullOrWhiteSpace(_options.BearerToken))
        {
            throw new InvalidOperationException(
                $"Dispatcher:BearerToken is required to issue callback tokens for tenant " +
                $"'{GuidFormatter.Format(tenantId)}'.");
        }

        return DeriveSigningKey(_options.BearerToken);
    }

    private static byte[] DeriveSigningKey(string token)
    {
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
