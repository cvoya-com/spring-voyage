// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// Thrown when a stored secret's ciphertext is present but the resolver
/// cannot recover the plaintext — for example because the at-rest
/// encryption key rotated since the value was written, the envelope was
/// corrupted, or the (tenantId, storeKey) tuple bound into the envelope's
/// Additional Authenticated Data no longer matches.
///
/// <para>
/// This is a <b>domain-level "unreadable" signal</b>, distinct from
/// "no slot exists" (which the resolver surfaces as a null value). It
/// lets callers separate two very different operational states:
/// "nothing configured, configure it" vs. "something configured but the
/// platform can't decrypt it, rotate the key or re-seed the slot". Prior
/// to this exception, a raw <see cref="System.Security.Cryptography.CryptographicException"/>
/// would bubble out of the encryptor and crash callers with a 500.
/// </para>
///
/// <para>
/// Every caller of <see cref="ISecretResolver"/> that surfaces status to
/// an operator (the credential-status endpoint, CLI health probes) MUST
/// catch this exception and map it to a structured "unreadable" state.
/// Callers that cannot proceed without the plaintext (dispatch-time
/// credential resolution, connector auth) may let it propagate — the
/// exception will already log with enough context to diagnose.
/// </para>
/// </summary>
public class SecretUnreadableException : SpringException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SecretUnreadableException"/>
    /// class with a default message.
    /// </summary>
    public SecretUnreadableException()
        : base("Failed to decrypt the stored secret envelope. "
            + "The at-rest encryption key may have rotated, the ciphertext "
            + "may be corrupted, or the envelope was bound to a different "
            + "(tenantId, storeKey) tuple than the resolver is reading under.")
    {
    }

    /// <summary>
    /// Initializes a new instance with a specified error message.
    /// </summary>
    public SecretUnreadableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with a specified error message and
    /// the underlying cryptographic failure.
    /// </summary>
    public SecretUnreadableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
