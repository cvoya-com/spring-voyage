// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Stores pending-authorization state between <c>/authorize</c> and
/// <c>/callback</c>. Abstracted so the private cloud repo (or a multi-host
/// deployment) can plug in a distributed backing store — see follow-up
/// issue for the multi-host coordination design.
///
/// <para>
/// <b>One-time-use.</b> The callback MUST consume the entry via
/// <see cref="ConsumeAsync"/>, not just read it — the default impl deletes
/// the entry atomically so a replayed callback fails closed.
/// </para>
/// </summary>
public interface IOAuthStateStore
{
    /// <summary>
    /// Writes a pending-authorization entry. Overwrites any prior entry
    /// with the same <c>state</c> value — collisions are vanishingly
    /// unlikely given the 128-bit random space but should not corrupt the
    /// store.
    /// </summary>
    /// <param name="entry">The pending-authorization entry.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(OAuthStateEntry entry, CancellationToken ct);

    /// <summary>
    /// Atomically retrieves and deletes the entry for the given state
    /// value. Returns <c>null</c> if the state is unknown or has expired.
    /// </summary>
    /// <param name="state">The state value from the callback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The consumed entry, or <c>null</c> if not found / expired.</returns>
    Task<OAuthStateEntry?> ConsumeAsync(string state, CancellationToken ct);
}