// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Secrets;

/// <summary>
/// Application-layer envelope encryption/decryption used by the OSS
/// <see cref="DaprStateBackedSecretStore"/> so plaintext never lands in
/// the backing Dapr state store (e.g. Redis in local dev). Exposed as an
/// interface so the private cloud repo can substitute a KMS-backed
/// implementation without touching call sites.
///
/// <para>
/// The envelope format is versioned. Version 1 is AES-GCM-256 with a
/// random 12-byte nonce, a 16-byte authentication tag, and the tuple
/// <c>(tenantId, storeKey)</c> bound in as Additional Authenticated
/// Data. Callers pass <c>tenantId</c> and <c>storeKey</c> on every
/// call; the encryptor owns the key material and envelope layout.
/// </para>
/// </summary>
public interface ISecretsEncryptor
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and returns a base64-encoded
    /// envelope that embeds the version byte, nonce, ciphertext, and
    /// authentication tag.
    /// </summary>
    /// <param name="plaintext">The value to encrypt. Must not be null.</param>
    /// <param name="tenantId">Tenant id, bound in as AAD.</param>
    /// <param name="storeKey">Opaque store key, bound in as AAD.</param>
    string Encrypt(string plaintext, Guid tenantId, string storeKey);

    /// <summary>
    /// Decrypts a value previously produced by <see cref="Encrypt"/>.
    /// Values without the version prefix are treated as legacy plaintext
    /// and returned verbatim so the store can read pre-encryption data
    /// (see <see cref="DaprStateBackedSecretStore"/>).
    /// </summary>
    /// <param name="value">The persisted envelope (or legacy plaintext).</param>
    /// <param name="tenantId">Tenant id, expected to match the encryption AAD.</param>
    /// <param name="storeKey">Store key, expected to match the encryption AAD.</param>
    /// <param name="wasEnveloped">True if the value was a valid envelope; false if treated as legacy plaintext.</param>
    /// <returns>The plaintext value.</returns>
    string Decrypt(string value, Guid tenantId, string storeKey, out bool wasEnveloped);
}
