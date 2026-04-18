// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Cloning;

/// <summary>
/// Addressable scope for a persistent <see cref="AgentCloningPolicy"/>.
/// Paired with an opaque target id (agent id or tenant id) by
/// <c>IAgentCloningPolicyRepository</c> so a single repository interface
/// can back both surfaces without a second store.
/// </summary>
public enum CloningPolicyScope
{
    /// <summary>
    /// The policy applies to one specific agent. The target id is the
    /// agent's identifier (the same id the clone endpoints use).
    /// </summary>
    Agent,

    /// <summary>
    /// The policy applies tenant-wide — it is consulted when an agent
    /// has no agent-scoped policy. The target id is the tenant id
    /// returned by <c>ITenantContext</c>.
    /// </summary>
    Tenant,
}