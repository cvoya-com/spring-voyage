// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using System.Collections.Concurrent;
using System.Text;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Materialises per-invocation agent workspaces so the dispatcher can launch a
/// container whose workspace mount is already populated.
/// </summary>
/// <remarks>
/// <para>
/// Two destinations, picked by whether the workspace mount path coincides with
/// a named-volume mount the caller already requested (#2608):
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Persistent volume.</b> When the workspace mount path equals the
///     in-container path of one of the requested <c>volumeName:containerPath</c>
///     mounts, the files are written directly into that volume's host mount
///     point. The container then carries exactly one workspace mount — the
///     per-agent persistent volume — and the files survive across container
///     restarts. No per-container cleanup: the volume is reclaimed only on
///     agent undeploy by <c>AgentVolumeManager</c>.
///   </item>
///   <item>
///     <b>Bind-mount fallback.</b> When no requested volume mount coincides
///     with the workspace mount path (test harnesses, or a launcher with no
///     persistent volume), the files are written into a fresh per-invocation
///     directory under <c>Dispatcher:WorkspaceRoot</c> and the caller appends
///     the returned bind-mount spec. That directory is cleaned up when the
///     container exits — the original issue #1042 behaviour.
///   </item>
/// </list>
/// <para>
/// Files are written verbatim — the materializer does not interpret content,
/// re-encode, or apply templating. Relative paths may use either forward or
/// platform-native separators; absolute paths and <c>..</c> traversals are
/// rejected so workers cannot escape the workspace root.
/// </para>
/// </remarks>
public interface IWorkspaceMaterializer
{
    /// <summary>
    /// Materialises <paramref name="workspace"/> ahead of container start.
    /// When <paramref name="requestedVolumeMounts"/> contains a
    /// <c>volumeName:containerPath</c> entry whose container path equals
    /// <see cref="WorkspaceRequest.MountPath"/>, the files are written into
    /// that named volume's host mount point and the returned
    /// <see cref="MaterializedWorkspace.MountSpec"/> is <c>null</c> (the
    /// caller already has the volume in its mount list). Otherwise the files
    /// land in a fresh per-invocation host directory and
    /// <see cref="MaterializedWorkspace.MountSpec"/> carries the bind-mount
    /// spec the caller must append. Throws
    /// <see cref="InvalidOperationException"/> for invalid relative paths.
    /// </summary>
    Task<MaterializedWorkspace> MaterializeAsync(
        WorkspaceRequest workspace,
        IReadOnlyList<string>? requestedVolumeMounts = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that <paramref name="materialized"/> belongs to
    /// <paramref name="containerId"/> so a later <see cref="CleanupForContainer"/>
    /// call (driven by <c>DELETE /v1/containers/{id}</c>) can delete it.
    /// </summary>
    void TrackForContainer(string containerId, MaterializedWorkspace materialized);

    /// <summary>
    /// Deletes any workspace previously associated with
    /// <paramref name="containerId"/>. Safe to call when nothing is tracked.
    /// </summary>
    void CleanupForContainer(string containerId);

    /// <summary>
    /// Deletes the per-invocation host directory pointed to by
    /// <paramref name="materialized"/> when it is a bind-mount fallback. A
    /// volume-backed workspace (<see cref="MaterializedWorkspace.MountSpec"/>
    /// is <c>null</c>) is a no-op — the per-agent persistent volume is
    /// reclaimed only on agent undeploy. Tolerates missing directories —
    /// callers may invoke this from a <c>finally</c> block without
    /// pre-checking existence.
    /// </summary>
    void Cleanup(MaterializedWorkspace materialized);
}

/// <summary>
/// Result of <see cref="IWorkspaceMaterializer.MaterializeAsync"/>.
/// </summary>
/// <param name="HostDirectory">
/// Absolute host path the files were written to: a fresh per-invocation
/// directory for the bind-mount fallback, or the named volume's host mount
/// point for the persistent-volume path.
/// </param>
/// <param name="MountPath">In-container path of the workspace mount.</param>
/// <param name="MountSpec">
/// Ready-to-pass bind-mount spec (<c>host:container</c>) the caller must
/// append to the container's mount list — populated only for the bind-mount
/// fallback. <c>null</c> when the files were written into a named volume the
/// caller already mounts (the persistent-volume path); in that case the
/// caller adds nothing and there is no per-container cleanup.
/// </param>
public record MaterializedWorkspace(
    string HostDirectory,
    string MountPath,
    string? MountSpec);

/// <summary>
/// Default <see cref="IWorkspaceMaterializer"/>. Uses the local filesystem
/// rooted at <see cref="DispatcherOptions.WorkspaceRoot"/>.
/// </summary>
public sealed class WorkspaceMaterializer(
    IOptions<DispatcherOptions> options,
    IWorkspaceVolumeLocator volumeLocator,
    ILoggerFactory loggerFactory) : IWorkspaceMaterializer
{
    // The static Encoding.UTF8 emits a UTF-8 BOM. A BOM at the start of a
    // workspace file breaks strict JSON consumers — the sidecar's per-turn
    // `.mcp.json` callback-token refresh does `JSON.parse` and throws on the
    // leading U+FEFF. Workspace files MUST be written as UTF-8 with no BOM.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger _logger = loggerFactory.CreateLogger<WorkspaceMaterializer>();
    private readonly DispatcherOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, MaterializedWorkspace> _byContainer =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<MaterializedWorkspace> MaterializeAsync(
        WorkspaceRequest workspace,
        IReadOnlyList<string>? requestedVolumeMounts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(workspace.MountPath))
        {
            throw new InvalidOperationException("Workspace mount path is required.");
        }
        if (!workspace.MountPath.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"Workspace mount path '{workspace.MountPath}' must be an absolute path inside the container.");
        }

        // #2608: when the workspace mount path coincides with a named-volume
        // mount the caller already requested, write the launcher files into
        // that volume's host mount point rather than into a separate
        // per-invocation bind mount. The container then carries exactly one
        // workspace mount — the per-agent persistent volume — and the files
        // (CLAUDE.md, .mcp.json, …) sit alongside the CLI's session state and
        // the bridge's marker files instead of in a divergent directory.
        var volumeName = FindCoincidingVolume(workspace.MountPath, requestedVolumeMounts);
        if (volumeName is not null)
        {
            var mountpoint = await volumeLocator
                .ResolveVolumeMountpointAsync(volumeName, cancellationToken);
            if (mountpoint is not null)
            {
                await WriteFilesAsync(mountpoint, workspace, cancellationToken);
                _logger.LogInformation(
                    "Materialised workspace into volume={VolumeName} mountpoint={Mountpoint} "
                    + "mount={MountPath} files={FileCount}",
                    volumeName, mountpoint, workspace.MountPath, workspace.Files.Count);

                // MountSpec is null: the caller already mounts this volume, so
                // there is nothing to append and nothing to clean up per
                // container — the volume is reclaimed only on agent undeploy.
                return new MaterializedWorkspace(mountpoint, workspace.MountPath, MountSpec: null);
            }

            // Volume named but its mount point did not resolve (e.g. the
            // volume was not created yet, or a remote driver). Fall through to
            // the bind-mount path so the launch still gets a populated
            // workspace rather than silently launching an empty one.
            _logger.LogWarning(
                "Workspace mount {MountPath} coincides with volume {VolumeName} but its host "
                + "mount point did not resolve; falling back to a per-invocation bind mount.",
                workspace.MountPath, volumeName);
        }

        var root = _options.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "Dispatcher:WorkspaceRoot is not configured. Set it to a writable host path (default: "
                + DispatcherOptions.DefaultWorkspaceRoot + ").");
        }

        Directory.CreateDirectory(root);

        var subdirName = "spring-ws-" + Guid.NewGuid().ToString("N");
        var hostDir = Path.Combine(root, subdirName);
        Directory.CreateDirectory(hostDir);
        ApplyWorldReadable(hostDir);

        try
        {
            await WriteFilesAsync(hostDir, workspace, cancellationToken);
        }
        catch
        {
            // Roll the host dir back if we failed mid-write so we don't leak
            // a half-populated workspace into the workspace root.
            TryDelete(hostDir);
            throw;
        }

        var mountSpec = $"{hostDir}:{workspace.MountPath}";
        _logger.LogInformation(
            "Materialised workspace dir={HostDir} mount={MountPath} files={FileCount}",
            hostDir, workspace.MountPath, workspace.Files.Count);

        return new MaterializedWorkspace(hostDir, workspace.MountPath, mountSpec);
    }

    /// <summary>
    /// Returns the volume name of the requested <c>volumeName:containerPath</c>
    /// mount whose container path equals <paramref name="mountPath"/>, or
    /// <c>null</c> when none coincides. The comparison normalises a single
    /// trailing slash on either side so <c>/spring/workspace/</c> and
    /// <c>/spring/workspace</c> are treated as the same path.
    /// </summary>
    private static string? FindCoincidingVolume(
        string mountPath,
        IReadOnlyList<string>? requestedVolumeMounts)
    {
        if (requestedVolumeMounts is not { Count: > 0 })
        {
            return null;
        }

        var target = NormalisePath(mountPath);
        foreach (var spec in requestedVolumeMounts)
        {
            if (string.IsNullOrWhiteSpace(spec))
            {
                continue;
            }

            // A mount spec is `source:containerPath[:opts]`. The source is a
            // named volume only when it is not an absolute host path — a host
            // bind mount (`/host/dir:/container/dir`) is never a named volume,
            // and writing into a volume requires the source to be a name.
            var colon = spec.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var source = spec[..colon];
            var rest = spec[(colon + 1)..];
            if (source.StartsWith('/'))
            {
                // Host bind mount, not a named volume.
                continue;
            }

            // The container path runs to the next `:` (mount options) or end.
            var optColon = rest.IndexOf(':');
            var containerPath = optColon >= 0 ? rest[..optColon] : rest;
            if (NormalisePath(containerPath) == target)
            {
                return source;
            }
        }

        return null;
    }

    /// <summary>Strips a single trailing slash so path comparisons are slash-agnostic.</summary>
    private static string NormalisePath(string path)
        => path.Length > 1 && path.EndsWith('/') ? path[..^1] : path;

    /// <summary>
    /// Writes <paramref name="workspace"/>'s files into <paramref name="destDir"/>,
    /// creating parent directories as needed and applying the world-readable /
    /// world-writable treatment the in-container agent (a different uid) needs.
    /// </summary>
    private async Task WriteFilesAsync(
        string destDir,
        WorkspaceRequest workspace,
        CancellationToken cancellationToken)
    {
        // World-readable+executable on directories and world-readable+writable
        // on files. The launched container runs as a *different* uid (the
        // agent user inside the image, typically uid 1000) than the dispatcher
        // process (the host user, frequently uid 501 on macOS). Without an
        // explicit chmod the dispatcher's umask wins, producing 0700 dirs and
        // 0644 files the in-container agent cannot enter or rewrite — and the
        // sidecar rewrites `.mcp.json` in place every turn to refresh the
        // per-turn MCP token (#2580). We don't write secrets here; the bearer
        // token is in the env var stream, not the workspace.
        foreach (var (relativePath, content) in workspace.Files)
        {
            var safePath = SanitizeRelativePath(relativePath, destDir);
            var parent = Path.GetDirectoryName(safePath);
            if (!string.IsNullOrEmpty(parent) && parent != destDir)
            {
                Directory.CreateDirectory(parent);
                ApplyWorldReadable(parent);
            }

            await File.WriteAllTextAsync(safePath, content ?? string.Empty, Utf8NoBom, cancellationToken);
            ApplyWorldWritableFile(safePath);
        }
    }

    /// <summary>
    /// Sets a directory's mode to 0755 on Unix-like systems. No-op on Windows
    /// (where the file ACL model is incompatible and the dispatcher does not
    /// run on Windows in any deployment we ship).
    /// </summary>
    private static void ApplyWorldReadable(string dir)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        const UnixFileMode dirMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(dir, dirMode);
    }

    /// <summary>
    /// Sets a file's mode to 0666 on Unix-like systems. No-op on Windows.
    /// </summary>
    /// <remarks>
    /// World-<i>writable</i>, not just world-readable: the in-container
    /// agent runs as a different uid than the dispatcher (see
    /// <see cref="ApplyWorldReadable"/>), and the sidecar rewrites
    /// <c>.mcp.json</c> in place on every turn to refresh the runtime
    /// callback token (#2580). A 0644 file owned by the dispatcher uid is
    /// not writable by the agent uid — the refresh fails with
    /// <c>EACCES</c>, the token is never rotated, and it expires mid-turn.
    /// The workspace is a per-invocation, single-container, ephemeral bind
    /// mount, so world-writable here grants nothing across a trust
    /// boundary.
    /// </remarks>
    private static void ApplyWorldWritableFile(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        const UnixFileMode fileMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
        File.SetUnixFileMode(path, fileMode);
    }

    /// <inheritdoc />
    public void TrackForContainer(string containerId, MaterializedWorkspace materialized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(materialized);

        // A volume-backed workspace (MountSpec == null) lives in the per-agent
        // persistent volume, reclaimed only on agent undeploy by
        // AgentVolumeManager — never on container stop. Only bind-mount
        // fallbacks need per-container cleanup tracking (#2608).
        if (materialized.MountSpec is null)
        {
            return;
        }

        _byContainer[containerId] = materialized;
        _logger.LogDebug(
            "Tracking workspace dir={HostDir} for container {ContainerId}",
            materialized.HostDirectory, containerId);
    }

    /// <inheritdoc />
    public void CleanupForContainer(string containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return;
        }
        if (_byContainer.TryRemove(containerId, out var materialized))
        {
            Cleanup(materialized);
        }
    }

    /// <inheritdoc />
    public void Cleanup(MaterializedWorkspace materialized)
    {
        if (materialized is null)
        {
            return;
        }

        // Volume-backed workspaces (MountSpec == null) must NOT be deleted on
        // container exit — the per-agent persistent volume survives restarts
        // and is reclaimed only on agent undeploy. Only the bind-mount
        // fallback owns a disposable per-invocation host directory (#2608).
        if (materialized.MountSpec is null)
        {
            return;
        }

        TryDelete(materialized.HostDirectory);
    }

    private void TryDelete(string hostDir)
    {
        try
        {
            if (Directory.Exists(hostDir))
            {
                Directory.Delete(hostDir, recursive: true);
                _logger.LogInformation("Cleaned up workspace dir={HostDir}", hostDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete workspace dir={HostDir}; leaving in place for operator inspection.",
                hostDir);
        }
    }

    private static string SanitizeRelativePath(string relative, string hostDir)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            throw new InvalidOperationException("Workspace file path must not be empty.");
        }

        // Reject anything that looks like an absolute path on either platform.
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            throw new InvalidOperationException(
                $"Workspace file path '{relative}' must be relative.");
        }

        // Normalise separators and resolve the full path, then ensure it stays
        // inside hostDir to block ../../etc/passwd-style escapes.
        var normalized = relative.Replace('\\', '/');
        var combined = Path.GetFullPath(Path.Combine(hostDir, normalized));
        var rootFull = Path.GetFullPath(hostDir) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootFull, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Workspace file path '{relative}' escapes the workspace root.");
        }
        return combined;
    }
}
