// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Runtime;

/// <summary>
/// Returns the per-tenant symmetric signing-key bytes used to sign and
/// verify per-invocation callback tokens (see <see cref="CallbackToken"/>).
/// </summary>
/// <remarks>
/// <para>
/// The OSS deployment ships a single-tenant default backed by configuration
/// (a deployment-time secret per tenant id). The cloud overlay swaps in a
/// KMS / HSM-backed implementation that derives or fetches the key for the
/// tenant — the abstraction exists precisely so the swap is a DI
/// registration and not a code change.
/// </para>
/// <para>
/// Implementations must return at least 32 bytes (256 bits) of key material
/// for HMAC-SHA-256. A return shorter than that, or an empty span, is a
/// configuration error the host treats as fatal at startup.
/// </para>
/// </remarks>
public interface ITenantSigningKeyProvider
{
    /// <summary>
    /// Returns the symmetric signing key bytes for the given tenant. The
    /// caller copies the bytes into a transient
    /// <c>Microsoft.IdentityModel.Tokens.SymmetricSecurityKey</c>; the
    /// provider is free to return cached arrays as long as their contents
    /// stay stable for the tenant's lifetime.
    /// </summary>
    /// <param name="tenantId">The tenant whose key to return.</param>
    /// <returns>The signing key bytes for the tenant.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when no signing key is configured for the tenant.
    /// </exception>
    byte[] GetSigningKey(Guid tenantId);
}
