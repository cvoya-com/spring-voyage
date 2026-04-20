// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.CredentialHealth;

/// <summary>
/// Persistent state machine for a stored credential. Distinct from
/// <see cref="AgentRuntimes.CredentialValidationStatus"/>, which is the
/// per-attempt signal: a single network error does not flip the
/// persistent status to <see cref="Invalid"/>, but a 401 from the backing
/// service does.
/// </summary>
public enum CredentialHealthStatus
{
    /// <summary>
    /// The credential has not been validated yet, or no signal is
    /// available. Default for a freshly-recorded row.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The credential was accepted by the backing service on its most
    /// recent check.
    /// </summary>
    Valid = 1,

    /// <summary>
    /// The credential is syntactically well-formed but rejected by the
    /// backing service (typical 401 response).
    /// </summary>
    Invalid = 2,

    /// <summary>
    /// The backing service reports the credential has expired (for
    /// services that distinguish expiry from revocation — e.g. OAuth
    /// <c>invalid_grant</c> with <c>token_expired</c>). Equivalent to
    /// <see cref="Invalid"/> for consumers that don't care about the
    /// reason.
    /// </summary>
    Expired = 3,

    /// <summary>
    /// The backing service reports the credential has been revoked or
    /// the caller is forbidden (typical 403 response on an endpoint that
    /// accepted the same credential before).
    /// </summary>
    Revoked = 4,
}