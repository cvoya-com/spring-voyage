// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Outcome of
/// <see cref="IAgentRuntime.ValidateCredentialAsync(string, System.Threading.CancellationToken)"/>.
/// The credential-health store maps these values to its own state machine;
/// treat these as the raw signal from a single validation attempt.
/// </summary>
public enum CredentialValidationStatus
{
    /// <summary>
    /// Validation has not been attempted, or its result cannot be determined
    /// (e.g. the runtime requires no credential and therefore nothing was
    /// checked). Callers should treat this as "pending" rather than "good".
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The credential is accepted by the backing service.
    /// </summary>
    Valid = 1,

    /// <summary>
    /// The credential is syntactically well-formed but rejected by the
    /// backing service (401/403 or equivalent).
    /// </summary>
    Invalid = 2,

    /// <summary>
    /// Validation could not reach the backing service (DNS, TLS, timeout,
    /// 5xx). The credential's validity is unknown; callers may retry.
    /// </summary>
    NetworkError = 3,
}