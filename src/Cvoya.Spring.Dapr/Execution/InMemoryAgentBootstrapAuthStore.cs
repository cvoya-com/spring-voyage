// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Collections.Concurrent;
using System.Security.Cryptography;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// In-process <see cref="IAgentBootstrapAuthStore"/> (ADR-0055 §8). One
/// opaque bearer per agent, kept in a concurrent dictionary keyed by
/// agentId. v0.1 runs a single worker, so a remote store is not required
/// (see ADR-0055 "Revisit criteria").
/// </summary>
/// <remarks>
/// Tokens are 256-bit random secrets formatted as 64-char lowercase hex.
/// <see cref="Validate"/> uses <see cref="CryptographicOperations.FixedTimeEquals"/>
/// to keep the comparison constant-time regardless of token prefix.
/// </remarks>
public sealed class InMemoryAgentBootstrapAuthStore : IAgentBootstrapAuthStore
{
    private readonly ConcurrentDictionary<string, string> _tokensByAgentId =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Issue(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return _tokensByAgentId.GetOrAdd(agentId, _ => GenerateToken());
    }

    /// <inheritdoc />
    public void Revoke(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }
        _tokensByAgentId.TryRemove(agentId, out _);
    }

    /// <inheritdoc />
    public bool Validate(string agentId, string token)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrEmpty(token))
        {
            return false;
        }
        if (!_tokensByAgentId.TryGetValue(agentId, out var expected))
        {
            return false;
        }

        // FixedTimeEquals demands equal-length spans. A length mismatch is
        // an immediate false — we are not leaking timing about the *valid*
        // token's length here because the valid token's length is a
        // platform-fixed constant (64 hex chars).
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(token);
        if (expectedBytes.Length != providedBytes.Length)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private static string GenerateToken()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexStringLower(buffer);
    }
}
