// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

/// <summary>
/// Runtime-author-facing client for Spring Voyage orchestration callbacks.
/// </summary>
public interface IOrchestrationClient
{
    /// <summary>Posts a result back to the dispatcher thread.</summary>
    Task PostResultAsync(string threadId, string result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delegates a sub-task to a target. ADR-0049 — the returned
    /// <see cref="DelegateResponse"/> is a delivery acknowledgement (the
    /// message reached the target's mailbox), never the target's response.
    /// </summary>
    Task<DelegateResponse> DelegateAsync(
        string threadId,
        string targetUnitId,
        string prompt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fans out a prompt to multiple targets. ADR-0049 — the returned
    /// <see cref="FanoutResponse"/> reports per-target delivery outcomes,
    /// not the targets' work products.
    /// </summary>
    Task<FanoutResponse> FanoutAsync(
        string threadId,
        IReadOnlyList<string> targetUnitIds,
        string prompt,
        CancellationToken cancellationToken = default);
}
