// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Outcome of
/// <see cref="IAgentRuntime.FetchLiveModelsAsync(string, System.Threading.CancellationToken)"/>.
/// Treat these as the raw signal from a single fetch attempt — callers may
/// retry <see cref="NetworkError"/> and should surface
/// <see cref="Unsupported"/> as a "this runtime cannot enumerate live models"
/// message rather than an error.
/// </summary>
public enum FetchLiveModelsStatus
{
    /// <summary>
    /// The fetch has not been attempted, or its result cannot be determined.
    /// Callers should treat this as "pending" rather than "good".
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The backing service returned a live model list. See
    /// <see cref="FetchLiveModelsResult.Models"/>.
    /// </summary>
    Success = 1,

    /// <summary>
    /// The backing service rejected the supplied credential (401/403 or
    /// equivalent). No model list is available.
    /// </summary>
    InvalidCredential = 2,

    /// <summary>
    /// The fetch could not reach the backing service (DNS, TLS, timeout,
    /// 5xx). The model list's liveness is unknown; callers may retry.
    /// </summary>
    NetworkError = 3,

    /// <summary>
    /// The runtime does not expose a live model-enumeration endpoint in
    /// its backing service. Callers should keep the runtime's seed
    /// catalog (see <see cref="IAgentRuntime.DefaultModels"/>) as the
    /// authoritative list and surface "refresh not supported" to
    /// operators.
    /// </summary>
    Unsupported = 4,
}