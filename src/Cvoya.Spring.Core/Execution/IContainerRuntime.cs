// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Lifecycle;

/// <summary>
/// Result returned by <see cref="IContainerRuntime.GetHealthAsync"/>. Models
/// the three states a container's built-in HEALTHCHECK can be in, plus the
/// degenerate case where no HEALTHCHECK was declared.
/// </summary>
/// <param name="Healthy">
/// <c>true</c> when the runtime reports the container as healthy or when
/// no HEALTHCHECK is declared (treating absence as healthy by convention).
/// <c>false</c> when the runtime reports <c>unhealthy</c> or when the
/// container is not running (exited, paused, …).
/// </param>
/// <param name="Detail">
/// Human-readable elaboration: the raw inspect status string, or
/// <c>"no healthcheck declared"</c> when <see cref="Healthy"/> is
/// <c>true</c> only because no HEALTHCHECK is defined.
/// </param>
public record ContainerHealth(bool Healthy, string? Detail);

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
    /// auth failure, tag-not-found) that the <c>ArtefactValidationWorkflow</c>
    /// surfaces as <see cref="Units.ArtefactValidationCodes.ImagePullFailed"/>
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
    /// running a one-shot <c>curl</c> in the container's network namespace.
    /// Returns <c>true</c> when the endpoint answers 2xx within the runtime's
    /// per-call timeout (the implementation is short-bounded; callers that
    /// want to wait for slow boots should poll).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the dispatcher-routed exec probe established by Stage 2 of
    /// #522 / #1063 and broadened in #2198 to cover daprd-readiness too.
    /// The probe runs inside the container so it works for sidecars on
    /// a private per-app network the dispatcher does not join (ADR 0028
    /// Decision A keeps the dispatcher off tenant networks; <c>podman
    /// exec</c> is the ADR-mandated mechanism to reach into a tenant
    /// container without joining its network).
    /// </para>
    /// <para>
    /// The container image must carry <c>curl</c> on its PATH. The Spring
    /// platform image (<c>localhost/spring-voyage:latest</c> — spring-api,
    /// spring-web, spring-worker) and the agent base images
    /// (<c>spring-voyage-agent</c>, agent-base, agent.dapr) all install
    /// curl explicitly. The upstream <c>daprio/daprd</c> image is
    /// distroless and ships no shell or HTTP client; daprd is therefore
    /// probed via <c>podman exec</c> into its <i>paired app container</i>
    /// (which lives on the same per-app bridge and can resolve daprd by
    /// container DNS name) — see <c>DaprSidecarManager.WaitForHealthyAsync</c>.
    /// </para>
    /// <para>
    /// The contract is deliberately narrower than a generic <c>exec</c>: a
    /// URL string and a boolean answer, no shell expansion, no stdout
    /// capture. That keeps the dispatcher's surface area and security
    /// posture (RCE) bounded while solving the only worker-side use cases
    /// that need exec — sidecar / agent health polling.
    /// </para>
    /// <para>
    /// This is the only HTTP-probe primitive the runtime exposes today —
    /// the previous <c>ProbeHttpFromHostAsync</c> and
    /// <c>ProbeHttpFromTransientContainerAsync</c> were collapsed into this
    /// method in #2198 because they had drifted into doing the same thing
    /// (running curl inside the target container's network namespace) via
    /// indirect paths. <c>podman exec</c> is the cheapest, ADR-0028-aligned
    /// way to enter that namespace from the dispatcher.
    /// </para>
    /// </remarks>
    /// <param name="containerId">Identifier of the container to probe inside.</param>
    /// <param name="url">URL to probe; typically a container-DNS-name URL such as <c>http://my-sidecar:3500/v1.0/healthz/outbound</c>, or a loopback URL such as <c>http://localhost:3500/v1.0/healthz</c>.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// <c>true</c> when the endpoint answered 2xx; <c>false</c> on any
    /// non-2xx, network error, missing <c>curl</c>, or unknown container.
    /// Callers that need to distinguish those cases should fall back to
    /// inspect / logs.
    /// </returns>
    Task<bool> ProbeContainerHttpAsync(string containerId, string url, CancellationToken ct = default);

    /// <summary>
    /// Blocks until the named (already-started) container exits, then returns
    /// a <see cref="ContainerResult"/> capturing the exit code and the
    /// container's stdout / stderr accumulated since launch. This is the
    /// "wait" half of the Start + Wait decomposition that <see cref="RunAsync"/>
    /// composes internally — call sites that need to interleave work between
    /// "container started" and "container exited" (e.g. probing a paired
    /// daprd sidecar via <see cref="ProbeContainerHttpAsync"/> while the
    /// app container runs) start with <see cref="StartAsync"/> and finish
    /// with this method.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations SHOULD shell out to <c>podman wait &lt;id&gt;</c> (or
    /// the equivalent) so the wait is event-driven rather than poll-based.
    /// After the container exits, the implementation reads the exit code
    /// from <c>podman wait</c>'s stdout and the container's accumulated
    /// stdout / stderr from <c>podman logs &lt;id&gt;</c>.
    /// </para>
    /// <para>
    /// This method is the dispatcher-routed primitive added in #2198 so
    /// <c>ContainerLifecycleManager.LaunchWithSidecarAsync</c> can probe
    /// daprd by exec'ing into the app container (which only exists once
    /// the app has been Start'd). See ADR 0028 Decision A for why the
    /// dispatcher cannot dial daprd directly from its own process.
    /// </para>
    /// </remarks>
    /// <param name="containerId">Identifier of the container to wait on.</param>
    /// <param name="ct">A token to cancel the operation. Cancellation does NOT stop the underlying container — callers that want to abort should call <see cref="StopAsync"/> as well.</param>
    /// <returns>The result of the container execution.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the container id is unknown to the runtime, so the API layer can surface an HTTP 404.</exception>
    Task<ContainerResult> WaitForExitAsync(string containerId, CancellationToken ct = default);

    /// <summary>
    /// Ensures a named volume exists, creating it if it does not already.
    /// Idempotent — a volume that already exists is treated as success.
    /// Used by the agent workspace volume provisioning path (D3c) to
    /// guarantee the per-agent persistent volume is present before the
    /// agent container is started.
    /// </summary>
    /// <param name="volumeName">
    /// The name of the volume to create. Must be a non-empty, runtime-valid
    /// identifier. The caller is responsible for choosing a stable, unique
    /// name — see <see cref="AgentVolumeNaming"/> for the platform convention.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the runtime reports a non-zero exit that is not the
    /// "already exists" sentinel.
    /// </exception>
    Task EnsureVolumeAsync(string volumeName, CancellationToken ct = default);

    /// <summary>
    /// Removes a named volume. Idempotent — a volume that does not exist is
    /// treated as success so reclamation paths are safe to call after a
    /// partial-failure boot.
    /// </summary>
    /// <param name="volumeName">The name of the volume to remove.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the runtime reports a non-zero exit that is not the
    /// "no such volume" sentinel and is not a "volume is in use" condition
    /// (implementations SHOULD treat in-use as a warning and return rather
    /// than throwing, to avoid blocking reclamation of the registry entry).
    /// </exception>
    Task RemoveVolumeAsync(string volumeName, CancellationToken ct = default);

    /// <summary>
    /// Returns volume-level metrics (size in bytes, last-write timestamp)
    /// for the named volume. The platform collects these to emit
    /// volume-size and growth-rate telemetry per ADR-0029 — the content of
    /// the volume is never inspected.
    /// </summary>
    /// <param name="volumeName">The name of the volume to inspect.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// Metrics for the volume, or <c>null</c> when the volume does not
    /// exist or the runtime cannot determine the size (e.g. a remote
    /// volume driver). Callers MUST NOT throw on <c>null</c>.
    /// </returns>
    Task<VolumeMetrics?> GetVolumeMetricsAsync(string volumeName, CancellationToken ct = default);

    /// <summary>
    /// Reads the native HEALTHCHECK status for the named container by
    /// inspecting the runtime's container metadata. Returns a
    /// <see cref="ContainerHealth"/> that reflects the three-state result
    /// (healthy / unhealthy / no-healthcheck-declared) without requiring any
    /// binary (<c>wget</c>, <c>curl</c>) inside the workload image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the backing primitive for <c>GET /v1/containers/{id}/health</c>
    /// on the dispatcher (issue #1079). The dispatcher endpoint is the
    /// authoritative signal for non-sidecar consumers (the cloud overlay,
    /// monitoring, the <c>spring agent status</c> CLI) that need container
    /// health without being co-located on the container network.
    /// </para>
    /// <para>
    /// The implementation shells out to <c>&lt;binary&gt; inspect --format
    /// '{{.State.Health.Status}}'</c>. When the format template produces an
    /// empty string the container image declared no HEALTHCHECK — the method
    /// maps that to <c>Healthy = true, Detail = "no healthcheck declared"</c>
    /// so health-naive images don't show as unhealthy by default.
    /// </para>
    /// <para>
    /// The method throws <see cref="InvalidOperationException"/> only when
    /// the container id is unknown to the runtime (the API layer maps this
    /// to HTTP 404). It never throws for an unhealthy container — that state
    /// is expressed through <see cref="ContainerHealth.Healthy"/> = false.
    /// </para>
    /// </remarks>
    /// <param name="containerId">Identifier of the container to inspect.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="ContainerHealth"/> describing whether the container is
    /// currently healthy and why.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no container with <paramref name="containerId"/> is known
    /// to the runtime, so the API layer can surface an HTTP 404.
    /// </exception>
    Task<ContainerHealth> GetHealthAsync(string containerId, CancellationToken ct = default);

    /// <summary>
    /// Forwards a JSON HTTP <c>POST</c> into the named container's network
    /// namespace and returns the response. The dispatcher executes the
    /// request from inside the container (via <c>podman exec -i ... curl</c>)
    /// so the call works even when the worker process and the agent container
    /// live on different bridge networks — the worker is on the platform
    /// bridge (<c>spring-net</c>) and the agent is on a per-tenant bridge
    /// (<c>spring-tenant-&lt;id&gt;</c>) it cannot route into directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the dispatcher-proxied A2A message-send primitive that closes
    /// the second half of issue #1160 — the readiness probe is dispatched
    /// through <see cref="ProbeContainerHttpAsync"/>; this method covers the
    /// actual JSON-RPC <c>message/send</c> roundtrip the A2A SDK makes after
    /// readiness. Workers wire an
    /// <c>HttpMessageHandler</c> that translates outbound A2A SDK HTTP
    /// requests into calls on this primitive, so the SDK code path is
    /// preserved end-to-end (only the transport is swapped).
    /// </para>
    /// <para>
    /// The contract is intentionally narrow — POST + JSON body only — for
    /// the same reason <see cref="ProbeContainerHttpAsync"/> is narrow: a
    /// generic <c>exec</c> primitive widens the dispatcher's RCE surface,
    /// and the only worker-side caller today is the A2A SDK proxy. If a
    /// future caller needs GET, alternate content types, or response
    /// headers we will widen the contract deliberately rather than ship
    /// a general HTTP relay.
    /// </para>
    /// <para>
    /// The container image must carry <c>curl</c> on its PATH — Spring
    /// agent-base and the spring-voyage-agent image both ship it.
    /// (The previous transport used <c>wget --post-file=/dev/stdin</c>;
    /// that pattern only works for BusyBox wget — GNU wget on Debian
    /// rejects a non-seekable stdin with "Illegal seek".) Curl reads the
    /// body via <c>--data-binary @-</c>, returns 0 on a 2xx and non-zero
    /// on any &gt;=400 (with <c>-f</c>) or transport failure; the
    /// dispatcher reports 200 + body on success and collapses every
    /// failure mode (DNS, connection refused, missing curl, non-2xx,
    /// container gone) into 502 with an empty body. Finer-grained status
    /// discrimination is the caller's job (the A2A SDK retries the turn
    /// at its own layer).
    /// </para>
    /// </remarks>
    /// <param name="containerId">Identifier of the container to forward the request into.</param>
    /// <param name="url">
    /// In-container URL to POST to (e.g. <c>http://localhost:8999/</c>).
    /// The host portion is interpreted from inside the container, so
    /// <c>localhost</c> resolves to the agent's own loopback.
    /// </param>
    /// <param name="body">UTF-8 JSON payload to send as the request body.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// The proxied HTTP response. <see cref="ContainerHttpResponse.StatusCode"/>
    /// is 200 on a successful 2xx from the in-container endpoint and 502 on
    /// any failure.
    /// </returns>
    Task<ContainerHttpResponse> SendHttpJsonAsync(
        string containerId,
        string url,
        byte[] body,
        CancellationToken ct = default);
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
/// <param name="AdditionalNetworks">
/// Additional networks to attach the container to alongside <see cref="NetworkName"/>.
/// Emitted as repeated <c>--network</c> flags on the container <c>run</c> command (the
/// runtime accepts the option more than once on Podman and Docker 20.10+). Used by
/// <c>ContainerLifecycleManager</c> to dual-attach Dapr-fronted workflow / unit
/// containers to a tenant bridge (<c>spring-tenant-&lt;id&gt;</c>) on top of the
/// per-workflow app↔sidecar bridge — see ADR 0028 / issue #1166. <c>null</c> or
/// empty means "no additional networks". Names must be non-empty; the dispatcher
/// pre-creates them via <see cref="CreateNetworkAsync"/> if needed.
/// </param>
/// <param name="Labels">Optional container labels for identification and cleanup.</param>
/// <param name="DaprEnabled">Whether to attach a Dapr sidecar to this container.</param>
/// <param name="DaprAppId">The app-id for the Dapr sidecar.</param>
/// <param name="DaprAppPort">The port the app listens on for Dapr to call.</param>
/// <param name="DaprSidecarComponentsPath">
/// Optional host path to a Dapr components directory to bind-mount into the
/// <c>daprd</c> sidecar (overrides the <c>Dapr:Sidecar:ComponentsPath</c> default for
/// this launch only). Used by the Dapr Python agent, which needs Conversation +
/// workflow state components distinct from the platform <c>production</c> profile.
/// </param>
/// <param name="ExtraHosts">Additional <c>host:IP</c> entries to add to the container's <c>/etc/hosts</c>. Used to expose the MCP server to Linux containers via <c>host.docker.internal:host-gateway</c>.</param>
/// <param name="WorkingDirectory">Optional working directory inside the container.</param>
/// <param name="Entrypoint">
/// Override for the image's <c>ENTRYPOINT</c>. When non-null, the runtime
/// passes <c>--entrypoint &lt;Entrypoint&gt;</c> to podman / docker so the
/// container runs the specified binary instead of the image's declared
/// entrypoint. Used by the validation probe (#1686) to invoke a one-shot
/// tool (e.g. <c>claude --version</c>) on an image whose entrypoint is the
/// long-running A2A bridge sidecar.
/// </param>
public record ContainerConfig(
    string Image,
    IReadOnlyList<string>? Command = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    IReadOnlyList<string>? VolumeMounts = null,
    TimeSpan? Timeout = null,
    string? NetworkName = null,
    IReadOnlyList<string>? AdditionalNetworks = null,
    IReadOnlyDictionary<string, string>? Labels = null,
    bool DaprEnabled = false,
    string? DaprAppId = null,
    int? DaprAppPort = null,
    string? DaprSidecarComponentsPath = null,
    IReadOnlyList<string>? ExtraHosts = null,
    string? WorkingDirectory = null,
    string? ContainerName = null,
    string? Entrypoint = null);

/// <summary>
/// Response shape returned by <see cref="IContainerRuntime.SendHttpJsonAsync"/>.
/// Captured deliberately narrow — status code + body bytes — because the
/// dispatcher-proxied transport collapses every failure mode into a single
/// 502 anyway, and the only worker-side consumer (the A2A SDK proxy)
/// reconstructs an <see cref="System.Net.Http.HttpResponseMessage"/> from
/// these two fields and ignores response headers.
/// </summary>
/// <param name="StatusCode">HTTP status code (200 on 2xx, 502 on any failure).</param>
/// <param name="Body">UTF-8 response body bytes; empty on 502.</param>
public record ContainerHttpResponse(int StatusCode, byte[] Body);

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

/// <summary>
/// Volume-level metrics collected by
/// <see cref="IContainerRuntime.GetVolumeMetricsAsync"/>. The content of
/// the volume is never inspected — these are filesystem-metadata fields only,
/// suitable for size / growth-rate / last-write telemetry per ADR-0029.
/// </summary>
/// <param name="SizeBytes">
/// Current disk usage of the volume in bytes as reported by the container
/// runtime. May be <c>null</c> when the runtime cannot determine the size
/// (e.g. a remote or encrypted volume driver that does not expose usage).
/// </param>
/// <param name="LastWrite">
/// Timestamp of the most recent write to the volume's mount point as
/// reported by the container runtime inspection. May be <c>null</c> when
/// not available.
/// </param>
public record VolumeMetrics(long? SizeBytes, DateTimeOffset? LastWrite);
