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