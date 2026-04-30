// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Assembles the <c>IAgentContext</c> bootstrap bundle (D1 spec § 2) for a
/// single agent container launch. The bundle is split across two delivery
/// channels per the spec:
/// <list type="bullet">
///   <item><b>Environment variables</b> — scalar values (URLs, tokens, ids,
///   the <c>concurrent_threads</c> flag). Returned as
///   <see cref="AgentBootstrapContext.EnvironmentVariables"/>.</item>
///   <item><b>Mounted files</b> — structured payloads (agent definition YAML,
///   tenant-level config JSON) written to <c>/spring/context/</c>. Returned as
///   <see cref="AgentBootstrapContext.ContextFiles"/>.</item>
/// </list>
/// Implementations are the DI seam through which the private cloud host can
/// replace the default builder (e.g. with a tenant-scoped credential provider
/// or a different Bucket-2 URL resolution strategy) without forking the
/// launcher code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spec reference:</b> <c>docs/specs/agent-runtime-boundary.md</c> § 2
/// (IAgentContext payload shape, canonical env var names, canonical mount path
/// <c>/spring/context/</c>, delivery rules).
/// </para>
/// <para>
/// <b>Credential scope:</b> every token in the returned context MUST be
/// agent-scoped and per-launch — the builder MUST NOT reuse a token across
/// agent identities or across successive launches of the same agent. See
/// D1 spec § 2.1 and § 4.5.
/// </para>
/// </remarks>
public interface IAgentContextBuilder
{
    /// <summary>
    /// Builds the full <c>IAgentContext</c> bootstrap bundle for the given
    /// <paramref name="launchContext"/>. Returns a
    /// <see cref="AgentBootstrapContext"/> that the launcher merges into its
    /// <see cref="AgentLaunchSpec"/>.
    /// </summary>
    /// <param name="launchContext">
    /// The per-launch inputs: agent identity, MCP session credential, resolved
    /// prompt, and the agent definition (for serialisation into the mounted
    /// agent-definition file).
    /// </param>
    /// <param name="cancellationToken">Cancels the build.</param>
    Task<AgentBootstrapContext> BuildAsync(
        AgentLaunchContext launchContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mints a fresh <c>IAgentContext</c> bootstrap bundle for a supervisor-driven
    /// restart. The restart context carries only the agent's stable identity —
    /// no credential material — so that the supervisor MUST NOT cache tokens
    /// across launches (D1 spec § 2.2.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by <c>ContainerSupervisorActor.RestartAsync</c> in place of
    /// the original launch's env-var bundle, which expired or was never
    /// persisted (D3d — ADR-0029 § "Failure recovery").
    /// </para>
    /// <para>
    /// The default <see cref="AgentContextBuilder"/> implementation delegates
    /// to <see cref="BuildAsync"/> with minimal synthetic inputs — fresh tokens
    /// are minted per call there already. Cloud-overlay implementations get the
    /// same refresh point on the same seam without touching existing build logic.
    /// </para>
    /// <para>
    /// The returned bundle MUST NOT be persisted by the caller; it MUST be
    /// consumed once and discarded after the restarted container is launched.
    /// </para>
    /// </remarks>
    /// <param name="restartContext">
    /// The minimum agent identity needed to mint fresh credentials: agent id,
    /// tenant id, and optionally unit id. MUST NOT contain any credential or
    /// token material.
    /// </param>
    /// <param name="cancellationToken">Cancels the build.</param>
    Task<AgentBootstrapContext> RefreshForRestartAsync(
        SupervisorRestartContext restartContext,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The assembled <c>IAgentContext</c> bootstrap bundle for one container launch.
/// </summary>
/// <param name="EnvironmentVariables">
/// The canonical env vars defined in D1 spec § 2.2.1 (e.g.
/// <c>SPRING_TENANT_ID</c>, <c>SPRING_BUCKET2_URL</c>,
/// <c>SPRING_CONCURRENT_THREADS</c>). The launcher merges these into the
/// container's env-var map.
/// </param>
/// <param name="ContextFiles">
/// Files to write under <c>/spring/context/</c> inside the container (D1 spec
/// § 2.2.2). Keys are filenames relative to the mount point (e.g.
/// <c>agent-definition.yaml</c>, <c>tenant-config.json</c>). The launcher
/// merges these into its <see cref="AgentLaunchSpec.WorkspaceFiles"/> under
/// the <c>/spring/context/</c> sub-path.
/// </param>
public record AgentBootstrapContext(
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyDictionary<string, string> ContextFiles);

/// <summary>
/// The minimum agent identity the supervisor hands to
/// <see cref="IAgentContextBuilder.RefreshForRestartAsync"/> to mint fresh
/// credentials on a crash-driven container restart (D3d — ADR-0029 § "Failure
/// recovery", D1 spec § 2.2.3).
/// </summary>
/// <remarks>
/// Contains only stable identity fields — never credential or token material.
/// The supervisor persists these fields in <c>SupervisorState</c> so they
/// survive across Dapr actor deactivations.
/// </remarks>
/// <param name="AgentId">The stable agent identifier.</param>
/// <param name="TenantId">
/// The tenant the agent runs under. Defaults to <c>"default"</c> for
/// existing supervisors that pre-date this field (safe migration value).
/// </param>
/// <param name="UnitId">
/// The unit the agent is a member of, if applicable. <c>null</c> for
/// standalone agents.
/// </param>
/// <param name="ConcurrentThreads">
/// The resolved concurrent-threads policy for this agent. Defaults to
/// <c>true</c> (the spec default) for existing supervisors without this field.
/// </param>
public record SupervisorRestartContext(
    string AgentId,
    string TenantId = "default",
    string? UnitId = null,
    bool ConcurrentThreads = true);