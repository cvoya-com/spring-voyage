// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Resolves a named container volume to its host-side mount point so the
/// dispatcher can write files directly into the volume's backing directory
/// before a container that mounts it starts.
/// </summary>
/// <remarks>
/// <para>
/// This is a dispatcher-side capability: it is implemented by the real
/// process-backed container runtime (which can shell out to
/// <c>volume inspect</c>) and consumed by the workspace materialiser. It is
/// deliberately a <i>separate</i> interface from <see cref="IContainerRuntime"/>
/// — the worker-side HTTP proxy runtime never materialises workspaces, so
/// forcing a volume-mountpoint method onto <see cref="IContainerRuntime"/>
/// would oblige the proxy to carry an implementation it cannot honour.
/// </para>
/// <para>
/// Issue #2608: launcher-written workspace files (<c>CLAUDE.md</c>,
/// <c>.mcp.json</c>, …) must land inside the per-agent persistent volume —
/// the single workspace mount — rather than in a separate per-invocation
/// bind mount. Writing into the volume's host mount point before container
/// start is how the dispatcher does that.
/// </para>
/// </remarks>
public interface IWorkspaceVolumeLocator
{
    /// <summary>
    /// Returns the absolute host path the named volume is backed by, or
    /// <c>null</c> when the volume does not exist or the runtime cannot
    /// determine its mount point (e.g. a remote volume driver).
    /// </summary>
    /// <param name="volumeName">The named-volume identifier.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<string?> ResolveVolumeMountpointAsync(string volumeName, CancellationToken ct = default);
}
