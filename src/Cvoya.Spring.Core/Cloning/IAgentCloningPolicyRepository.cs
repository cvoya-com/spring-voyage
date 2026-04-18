// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Cloning;

/// <summary>
/// Persistence abstraction for <see cref="AgentCloningPolicy"/> values
/// keyed by <see cref="CloningPolicyScope"/> + target id (#416). Kept in
/// <c>Cvoya.Spring.Core</c> so the private cloud repo can supply a tenant-
/// scoped wrapper via DI without depending on <c>Cvoya.Spring.Dapr</c>.
/// </summary>
/// <remarks>
/// <para>
/// Repository contract: <see cref="GetAsync"/> always returns
/// <see cref="AgentCloningPolicy.Empty"/> when no row exists so callers
/// never have to branch on "is there a row". <see cref="SetAsync"/> with
/// <see cref="AgentCloningPolicy.Empty"/> is a valid "clear all
/// constraints" operation — the default implementation treats that as a
/// row deletion so the store reflects scopes that actually have a policy.
/// </para>
/// <para>
/// The interface deliberately doesn't expose a "resolve effective policy"
/// method — that lives on <see cref="IAgentCloningPolicyEnforcer"/>, which
/// walks agent-scoped first, then tenant-scoped. Keeping resolution out of
/// the repository lets the private cloud repo decorate the enforcer with
/// extra scopes (e.g. per-unit) without reshaping persistence.
/// </para>
/// </remarks>
public interface IAgentCloningPolicyRepository
{
    /// <summary>
    /// Returns the persisted policy for the scope and target, or
    /// <see cref="AgentCloningPolicy.Empty"/> when none has been
    /// persisted.
    /// </summary>
    /// <param name="scope">The policy scope (agent or tenant).</param>
    /// <param name="targetId">
    /// The target identifier — agent id for <see cref="CloningPolicyScope.Agent"/>,
    /// tenant id for <see cref="CloningPolicyScope.Tenant"/>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The persisted policy, or an empty policy if none.</returns>
    Task<AgentCloningPolicy> GetAsync(
        CloningPolicyScope scope,
        string targetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the policy for the given scope + target. Passing
    /// <see cref="AgentCloningPolicy.Empty"/> is a valid "clear all"
    /// operation; implementations are free to represent that as a delete.
    /// </summary>
    /// <param name="scope">The policy scope.</param>
    /// <param name="targetId">The target identifier.</param>
    /// <param name="policy">The policy to persist.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetAsync(
        CloningPolicyScope scope,
        string targetId,
        AgentCloningPolicy policy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any persisted policy for the scope + target. No-op when
    /// no row exists. Called by agent/tenant delete flows so orphan
    /// policy rows are not left behind.
    /// </summary>
    /// <param name="scope">The policy scope.</param>
    /// <param name="targetId">The target identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task DeleteAsync(
        CloningPolicyScope scope,
        string targetId,
        CancellationToken cancellationToken = default);
}