// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using System.Collections.Concurrent;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Materialises per-invocation agent workspaces on the dispatcher's host
/// filesystem and tracks them so the dispatcher can clean them up when the
/// associated container exits (or, for detached starts, when a stop call
/// arrives). Fixes the "worker writes to its own private /tmp, dispatcher
/// tries to bind-mount a non-existent host path" failure from issue #1042.
/// </summary>
/// <remarks>
/// Files are written verbatim — the materializer does not interpret content,
/// re-encode, or apply templating. Relative paths may use either forward or
/// platform-native separators; absolute paths and <c>..</c> traversals are
/// rejected so workers cannot escape the workspace root.
/// </remarks>
public interface IWorkspaceMaterializer
{
    /// <summary>
    /// Writes <paramref name="workspace"/> into a fresh per-invocation
    /// directory under <c>Dispatcher:WorkspaceRoot</c> and returns a handle
    /// describing the host directory and the bind-mount spec callers should
    /// append to the container's mount list. Throws
    /// <see cref="InvalidOperationException"/> for invalid relative paths.
    /// </summary>
    Task<MaterializedWorkspace> MaterializeAsync(
        WorkspaceRequest workspace,
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
    /// Deletes the on-disk directory pointed to by
    /// <paramref name="materialized"/>. Tolerates missing directories —
    /// callers may invoke this from a <c>finally</c> block without
    /// pre-checking existence.
    /// </summary>
    void Cleanup(MaterializedWorkspace materialized);
}

/// <summary>
/// Result of <see cref="IWorkspaceMaterializer.MaterializeAsync"/>.
/// </summary>
/// <param name="HostDirectory">Absolute host path of the per-invocation directory the dispatcher just created.</param>
/// <param name="MountPath">In-container path the host directory must be bind-mounted at.</param>
/// <param name="MountSpec">Ready-to-pass bind-mount spec (<c>host:container[:ro|:rw]</c>) for <see cref="ContainerConfig.VolumeMounts"/>.</param>
public record MaterializedWorkspace(
    string HostDirectory,
    string MountPath,
    string MountSpec);

/// <summary>
/// Default <see cref="IWorkspaceMaterializer"/>. Uses the local filesystem
/// rooted at <see cref="DispatcherOptions.WorkspaceRoot"/>.
/// </summary>
public sealed class WorkspaceMaterializer(
    IOptions<DispatcherOptions> options,
    ILoggerFactory loggerFactory) : IWorkspaceMaterializer
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<WorkspaceMaterializer>();
    private readonly DispatcherOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, MaterializedWorkspace> _byContainer =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<MaterializedWorkspace> MaterializeAsync(
        WorkspaceRequest workspace,
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

        try
        {
            foreach (var (relativePath, content) in workspace.Files)
            {
                var safePath = SanitizeRelativePath(relativePath, hostDir);
                var parent = Path.GetDirectoryName(safePath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                await File.WriteAllTextAsync(safePath, content ?? string.Empty, Encoding.UTF8, cancellationToken);
            }
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

    /// <inheritdoc />
    public void TrackForContainer(string containerId, MaterializedWorkspace materialized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(materialized);

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