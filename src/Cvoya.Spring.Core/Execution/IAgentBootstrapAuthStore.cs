// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Per-agent bootstrap-token authority (ADR-0055 §8). One opaque bearer
/// per agent, lifetime = agent lifetime: issued at agent-provision time,
/// revoked at undeploy. Presenting a token authenticates the bootstrap
/// pull for the agent it is bound to and only that agent.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from the per-turn MCP session store (see <c>McpServer</c>) —
/// the lifetimes do not match. Per-turn MCP tokens are revoked at turn
/// end; bootstrap tokens persist for the entire agent lifetime so a
/// container restart or post-creation configuration change can re-pull
/// without a new credential round-trip.
/// </para>
/// <para>
/// v0.1 implementation: in-memory, single-worker. Horizontal scale-out
/// of the worker re-opens the store-placement question (see ADR-0055
/// "Revisit criteria"); ADR-0054 names the same caveat for the MCP
/// session store.
/// </para>
/// </remarks>
public interface IAgentBootstrapAuthStore
{
    /// <summary>
    /// Returns the current bootstrap token for <paramref name="agentId"/>,
    /// minting one on first call. Idempotent: subsequent calls return the
    /// same token until a <see cref="Revoke"/> resets it. The token is an
    /// opaque, cryptographically random secret — server-side only, never
    /// derived from the agentId.
    /// </summary>
    string Issue(string agentId);

    /// <summary>
    /// Revokes the bootstrap token bound to <paramref name="agentId"/>.
    /// Idempotent — calling for an unknown agent or twice in a row is
    /// safe and silent. After revocation any <see cref="Validate"/> call
    /// with the old token returns <c>false</c>; the next
    /// <see cref="Issue"/> mints a fresh token.
    /// </summary>
    void Revoke(string agentId);

    /// <summary>
    /// Returns <c>true</c> iff <paramref name="token"/> is the live
    /// bootstrap token bound to <paramref name="agentId"/>. Constant-time
    /// comparison — implementations MUST NOT short-circuit on length or
    /// prefix mismatch.
    /// </summary>
    bool Validate(string agentId, string token);
}
