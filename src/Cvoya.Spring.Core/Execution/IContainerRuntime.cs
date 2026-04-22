// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Abstraction for running agent workloads in containers.
/// </summary>
public interface IContainerRuntime
{
    /// <summary>
    /// Pulls a container image from its registry so a subsequent
    /// <see cref="RunAsync(ContainerConfig, CancellationToken)"/> call can
    /// start it without an implicit pull. Separate from
    /// <see cref="RunAsync(ContainerConfig, CancellationToken)"/> because image
    /// pulls have distinct timeout and failure semantics (slow registry,
    /// auth failure, tag-not-found) that the <c>UnitValidationWorkflow</c>
    /// surfaces as <see cref="Units.UnitValidationCodes.ImagePullFailed"/>
    /// rather than a run-time failure.
    /// </summary>
    /// <param name="image">The fully-qualified container image reference (e.g. <c>ghcr.io/cvoya/claude:1.2.3</c>).</param>
    /// <param name="timeout">Maximum wall-clock time the runtime will allow the pull to run before aborting.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <exception cref="TimeoutException">Thrown when the pull does not complete within <paramref name="timeout"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the underlying CLI / dispatcher reports a non-zero exit.</exception>
    Task PullImageAsync(string image, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Launches a container with the given configuration and waits for it to complete.
    /// </summary>
    /// <param name="config">The container configuration.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The result of the container execution.</returns>
    Task<ContainerResult> RunAsync(ContainerConfig config, CancellationToken ct = default);

    /// <summary>
    /// Launches a container in detached mode, returning immediately with the
    /// container identifier. The container keeps running in the background
    /// until explicitly stopped via <see cref="StopAsync"/>.
    /// </summary>
    /// <param name="config">The container configuration.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The identifier of the started container.</returns>
    Task<string> StartAsync(ContainerConfig config, CancellationToken ct = default);

    /// <summary>
    /// Stops a running container by its identifier.
    /// </summary>
    /// <param name="containerId">The identifier of the container to stop.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task StopAsync(string containerId, CancellationToken ct = default);

    /// <summary>
    /// Reads the most recent log lines from a running (or recently-stopped)
    /// container. Implementations should cap the buffer at
    /// <paramref name="tail"/> lines to keep memory bounded. Used by
    /// <c>spring agent logs</c> for the persistent-agent surface (#396).
    /// </summary>
    /// <param name="containerId">The identifier of the container to read.</param>
    /// <param name="tail">Maximum number of log lines to return. Defaults to 200.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// The combined stdout+stderr tail as a single string. Returns an empty
    /// string when the container has produced no output yet. Throws if the
    /// container id is unknown so the caller can surface a 404.
    /// </returns>
    Task<string> GetLogsAsync(string containerId, int tail = 200, CancellationToken ct = default);

    /// <summary>
    /// Creates a container network with the given name. Idempotent: a network
    /// that already exists is treated as success so callers do not have to
    /// pre-check existence (the lifecycle manager re-uses a stable network
    /// name across restarts).
    /// </summary>
    /// <param name="name">The network name. Must be a non-empty, runtime-valid identifier.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">Thrown when the runtime reports a non-zero exit that is not the "already exists" sentinel.</exception>
    Task CreateNetworkAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Removes a container network by name. Idempotent: a network that does
    /// not exist is treated as success so the lifecycle manager's teardown
    /// path is safe to call after a partial-failure boot.
    /// </summary>
    /// <param name="name">The network name.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task RemoveNetworkAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Probes an HTTP endpoint reachable from inside the named container by
    /// running a one-shot <c>wget --spider</c> in the container's network
    /// namespace. Returns <c>true</c> when the endpoint answers 2xx within
    /// the runtime's per-call timeout (the implementation is short-bounded;
    /// callers that want to wait for slow boots should poll).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the dispatcher-routed replacement for the worker's old
    /// <c>podman exec &lt;id&gt; wget -q --spider &lt;url&gt;</c> sidecar-health
    /// pattern (Stage 2 of #522 / #1063). The probe runs inside the
    /// container so it works for sidecars on a private per-app network the
    /// worker does not share. The container image must carry <c>wget</c> on
    /// its PATH — the <c>daprio/daprd</c> image does.
    /// </para>
    /// <para>
    /// The contract is deliberately narrower than a generic <c>exec</c>: a
    /// URL string and a boolean answer, no shell expansion, no stdout
    /// capture. That keeps the dispatcher's surface area and security
    /// posture (RCE) bounded while solving the only worker-side use case
    /// that needed exec — sidecar health polling.
    /// </para>
    /// </remarks>
    /// <param name="containerId">Identifier of the container to probe inside.</param>
    /// <param name="url">URL to probe; typically a loopback URL such as <c>http://localhost:3500/v1.0/healthz</c>.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// <c>true</c> when the endpoint answered 2xx; <c>false</c> on any
    /// non-2xx, network error, missing <c>wget</c>, or unknown container.
    /// Callers that need to distinguish those cases should fall back to
    /// inspect / logs.
    /// </returns>
    Task<bool> ProbeContainerHttpAsync(string containerId, string url, CancellationToken ct = default);
}

/// <summary>
/// Configuration for launching a container.
/// </summary>
/// <param name="Image">The container image to run.</param>
/// <param name="Command">
/// Optional argv vector to set as the container's command. Each element
/// becomes one argv entry — the runtime does not shell-split or otherwise
/// re-parse the strings, so producers must split on whitespace themselves
/// (e.g. <c>["./daprd", "--app-id", "my-app"]</c>, never
/// <c>["./daprd --app-id my-app"]</c>). <c>null</c> or an empty list means
/// "use the image's default ENTRYPOINT/CMD". The list-typed shape replaces
/// the legacy <c>string?</c> field; see issue #1093 for the migration that
/// removed the dispatcher's whitespace-split fragility (cf. #1063).
/// </param>
/// <param name="EnvironmentVariables">Optional environment variables to set in the container.</param>
/// <param name="VolumeMounts">Optional volume mount specifications.</param>
/// <param name="Timeout">Optional timeout after which the container should be stopped.</param>
/// <param name="NetworkName">Optional Docker/Podman network to attach the container to.</param>
/// <param name="Labels">Optional container labels for identification and cleanup.</param>
/// <param name="DaprEnabled">Whether to attach a Dapr sidecar to this container.</param>
/// <param name="DaprAppId">The app-id for the Dapr sidecar.</param>
/// <param name="DaprAppPort">The port the app listens on for Dapr to call.</param>
/// <param name="ExtraHosts">Additional <c>host:IP</c> entries to add to the container's <c>/etc/hosts</c>. Used to expose the MCP server to Linux containers via <c>host.docker.internal:host-gateway</c>.</param>
/// <param name="WorkingDirectory">Optional working directory inside the container.</param>
/// <param name="Workspace">
/// Optional per-invocation workspace materialised on the dispatcher host. When
/// non-null, the dispatcher writes <see cref="ContainerWorkspace.Files"/> into
/// a fresh per-invocation directory on its own filesystem, bind-mounts that
/// directory at <see cref="ContainerWorkspace.MountPath"/> inside the
/// container, and cleans the directory up when the run completes (or, for
/// detached starts, when <c>StopAsync</c> is called for the resulting
/// container id). This is the seam that fixes the "worker writes to its own
/// /tmp, dispatcher tries to bind-mount a path that does not exist on the
/// host" failure mode in containerised dispatcher deployments — see issue
/// #1042.
/// </param>
public record ContainerConfig(
    string Image,
    IReadOnlyList<string>? Command = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    IReadOnlyList<string>? VolumeMounts = null,
    TimeSpan? Timeout = null,
    string? NetworkName = null,
    IReadOnlyDictionary<string, string>? Labels = null,
    bool DaprEnabled = false,
    string? DaprAppId = null,
    int? DaprAppPort = null,
    IReadOnlyList<string>? ExtraHosts = null,
    string? WorkingDirectory = null,
    ContainerWorkspace? Workspace = null);

/// <summary>
/// A per-invocation set of text files the dispatcher must materialise into a
/// fresh directory on its own filesystem and bind-mount into the launched
/// container at <see cref="MountPath"/>. Carried by
/// <see cref="ContainerConfig.Workspace"/>.
/// </summary>
/// <remarks>
/// <para>
/// The worker no longer writes the agent's <c>CLAUDE.md</c> / <c>AGENTS.md</c>
/// / <c>.mcp.json</c> files itself — those paths exist only on the worker
/// container's private filesystem and are invisible to the host's container
/// runtime. The launcher describes the desired workspace as a content map
/// keyed by relative path; the dispatcher creates the per-invocation directory
/// on its own filesystem (under <c>Dispatcher:WorkspaceRoot</c>), writes the
/// files, and uses that host path as the bind-mount source. See issue #1042.
/// </para>
/// <para>
/// Files are written verbatim — the dispatcher does not interpret content,
/// re-encode, or apply templating. Relative paths may contain forward
/// slashes; the dispatcher normalises directory separators before creating
/// parent directories. Absolute paths and <c>..</c> traversals are rejected.
/// </para>
/// </remarks>
/// <param name="MountPath">Absolute path inside the container where the dispatcher bind-mounts the materialised directory (e.g. <c>"/workspace"</c>).</param>
/// <param name="Files">File contents keyed by path relative to the workspace root (e.g. <c>"CLAUDE.md"</c>, <c>".mcp.json"</c>, <c>"sub/dir/file.txt"</c>).</param>
public record ContainerWorkspace(
    string MountPath,
    IReadOnlyDictionary<string, string> Files);

/// <summary>
/// Result of a container execution.
/// </summary>
/// <param name="ContainerId">The identifier of the container that ran.</param>
/// <param name="ExitCode">The exit code returned by the container process.</param>
/// <param name="StandardOutput">The standard output captured from the container.</param>
/// <param name="StandardError">The standard error captured from the container.</param>
public record ContainerResult(
    string ContainerId,
    int ExitCode,
    string StandardOutput,
    string StandardError);