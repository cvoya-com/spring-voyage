// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Assembles the canonical D1 env-var set the platform stamps on every
/// agent container launch (D1 spec § 2.2.1).
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0055 the launch-time context delivers env vars only — the
/// structured files that used to ride <c>/spring/context/</c>
/// (<c>agent-definition.yaml</c>, <c>tenant-config.json</c>) now live in
/// the agent-bootstrap bundle the sidecar pulls from the worker, written
/// under the per-member workspace mount alongside the rest of the
/// agent's workspace state.
/// </para>
/// <para>
/// Implementations are the DI seam through which the private cloud host
/// can replace the default builder (e.g. with a tenant-scoped credential
/// provider or a different Bucket-2 URL resolution strategy) without
/// forking the launcher code.
/// </para>
/// <para>
/// <b>Credential scope:</b> every token in the returned context MUST be
/// agent-scoped and per-launch — the builder MUST NOT reuse a token
/// across agent identities or across successive launches of the same
/// agent. See D1 spec § 2.1 and § 4.5.
/// </para>
/// </remarks>
public interface IAgentContextBuilder
{
    /// <summary>
    /// Builds the canonical D1 env-var set for the given
    /// <paramref name="launchContext"/>. The dispatcher merges the result
    /// into the launcher-emitted env-vars on the
    /// <see cref="AgentLaunchSpec"/>.
    /// </summary>
    Task<AgentBootstrapContext> BuildAsync(
        AgentLaunchContext launchContext,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The assembled D1-canonical env-var bundle for one container launch.
/// </summary>
/// <param name="EnvironmentVariables">
/// The canonical env vars defined in D1 spec § 2.2.1 (e.g.
/// <c>SPRING_TENANT_ID</c>, <c>SPRING_BUCKET2_URL</c>,
/// <c>SPRING_CONCURRENT_THREADS</c>). The dispatcher merges these into
/// the container's env-var map.
/// </param>
public record AgentBootstrapContext(
    IReadOnlyDictionary<string, string> EnvironmentVariables);
