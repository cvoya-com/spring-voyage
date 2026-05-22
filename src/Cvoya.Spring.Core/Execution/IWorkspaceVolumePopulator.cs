// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Populates a named container volume with launcher-written workspace files
/// before a container that mounts it starts.
/// </summary>
/// <remarks>
/// <para>
/// This is a dispatcher-side capability: it is implemented by the real
/// process-backed container runtime and consumed by the workspace
/// materialiser. It is deliberately a <i>separate</i> interface from
/// <see cref="IContainerRuntime"/> — the worker-side HTTP proxy runtime never
/// materialises workspaces, so forcing a volume-populate method onto
/// <see cref="IContainerRuntime"/> would oblige the proxy to carry an
/// implementation it cannot honour.
/// </para>
/// <para>
/// Issue #2608: launcher-written workspace files (<c>CLAUDE.md</c>,
/// <c>.mcp.json</c>, …) must land inside the per-agent persistent volume —
/// the single workspace mount — rather than in a separate per-invocation
/// bind mount.
/// </para>
/// <para>
/// The populate is routed <i>through the container runtime</i> rather than
/// writing into the volume's host backing directory directly. The dispatcher
/// does not always share a filesystem with the runtime's volume storage: on a
/// native Linux host it does, but when the runtime lives in a VM (e.g.
/// <c>podman machine</c> on macOS) the volume's backing path does not exist on
/// the dispatcher host at all — a direct write throws
/// <see cref="System.IO.DirectoryNotFoundException"/>. Routing the copy
/// through the runtime keeps the seeding correct regardless of where the
/// dispatcher process runs. See issue #2637 for the follow-up pull-model
/// proposal that would retire the push/seed model entirely.
/// </para>
/// </remarks>
public interface IWorkspaceVolumePopulator
{
    /// <summary>
    /// Copies the contents of <paramref name="stagingHostDir"/> into the named
    /// volume <paramref name="volumeName"/>, mediated by the container runtime.
    /// </summary>
    /// <param name="volumeName">The named-volume identifier to populate.</param>
    /// <param name="stagingHostDir">
    /// A dispatcher-host directory holding the files to copy. Its contents (not
    /// the directory itself) are copied into the volume root. The caller owns
    /// creating and deleting this directory.
    /// </param>
    /// <param name="helperImage">
    /// A container image used only to create the short-lived helper container
    /// that mounts the volume for the copy. The agent image being launched is
    /// the natural choice — it is needed for the launch regardless, so using it
    /// adds no extra image dependency.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// <c>true</c> when the files were copied into the volume; <c>false</c>
    /// when <paramref name="volumeName"/> does not exist — the caller falls
    /// back to a per-invocation bind mount in that case.
    /// </returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the volume exists but the runtime-mediated copy fails.
    /// </exception>
    Task<bool> TryPopulateVolumeAsync(
        string volumeName,
        string stagingHostDir,
        string helperImage,
        CancellationToken ct = default);
}
