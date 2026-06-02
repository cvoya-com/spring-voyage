// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// Platform-side service that fetches the array of
/// <see cref="ToolDefinition"/>s an agent advertises on its
/// <c>GET /a2a/tools</c> endpoint, and caches the result onto the agent
/// (or unit) row's <c>image_tools</c> column (#2336).
/// </summary>
/// <remarks>
/// <para>
/// Sub C of the Tools wave (#2332). The introspector is invoked from the
/// deploy path after the agent's HTTP listener has passed the readiness
/// probe (so the call is guaranteed to land on a live listener). It is
/// also invoked on image rotation — when the agent's
/// <c>execution.image</c> field changes — so the cached <c>image_tools</c>
/// reflects the new image's surface rather than the previous deployment's.
/// </para>
/// <para>
/// Failure semantics (#3003): the fetch is retried with backoff, then a
/// persistent non-200, timeout, transport error, or parse failure
/// <b>preserves</b> the previously-cached <c>image_tools</c> instead of
/// overwriting it with an empty array — a relaunched agent whose sidecar
/// is briefly not listening must not lose its real tool list. A
/// <i>successful</i> fetch that returns an empty array (a genuinely
/// tool-less image) is authoritative and overwrites the cache. The deploy
/// always succeeds — introspection is never load-bearing on boot.
/// </para>
/// <para>
/// The interface is the seam tests use to bypass the container-runtime
/// HTTP exec path: an in-process fake can return a deterministic array
/// without standing up an actual listener.
/// </para>
/// </remarks>
public interface IAgentToolsIntrospector
{
    /// <summary>
    /// Fetches the agent's <c>/a2a/tools</c> array and persists the result
    /// on the matching <c>agent_definitions</c> or
    /// <c>unit_definitions</c> row's <c>image_tools</c> column.
    /// </summary>
    /// <param name="agentId">
    /// The agent identity (entity Guid) — the persistence target on
    /// <c>agent_definitions.id</c> or <c>unit_definitions.id</c>.
    /// </param>
    /// <param name="containerId">
    /// The container id of the running agent; passed through to the
    /// container runtime so the HTTP call can route via the agent's own
    /// network namespace.
    /// </param>
    /// <param name="endpoint">
    /// The base HTTP endpoint of the agent listener (e.g.
    /// <c>http://localhost:8999/</c>). The introspector appends the
    /// <c>a2a/tools</c> path itself.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The fetched-and-persisted array (possibly empty for a tool-less image).
    /// Returns an empty array <b>without persisting</b> when the fetch failed
    /// after retries — in that case the previously-cached value is preserved.
    /// </returns>
    Task<IReadOnlyList<ToolDefinition>> IntrospectAndPersistAsync(
        Guid agentId,
        string containerId,
        Uri endpoint,
        CancellationToken cancellationToken = default);
}
