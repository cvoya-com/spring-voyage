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
    /// its PATH — the Spring agent images do; the upstream
    /// <c>daprio/daprd</c> image does <b>not</b> (it is effectively
    /// distroless), so probes against daprd sidecars must go through
    /// <see cref="ProbeHttpFromTransientContainerAsync"/> instead.
    /// </para>
    /// <para>
    /// The contract is deliberately narrower than a generic <c>exec</c>: a
    /// URL string and a boolean answer, no shell expansion, no stdout
    /// capture. That keeps the dispatcher's surface area and security
    /// posture (RCE) bounded while solving the only worker-side use case
    /// that needed exec — sidecar health polling.
    /// </para>
    /// <para>
    /// Prefer <see cref="ProbeHttpFromHostAsync"/> for A2A readiness probes
    /// on agent containers. The host-side probe does not require any binary
    /// (<c>wget</c>) inside the workload image and avoids the per-probe
    /// <c>podman exec</c> round-trip cost. This method is retained for
    /// cases where the target endpoint is only reachable from inside the
    /// container's own network namespace (e.g. a sidecar on a private
    /// per-app bridge that has no host-routable IP).
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

    /// <summary>
    /// Probes an HTTP endpoint from the host process by resolving the named
    /// container's host-visible IP address and issuing a plain HTTP GET.
    /// Returns <c>true</c> when the endpoint answers 2xx.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the replacement probe path for A2A readiness checks (issue
    /// #1175). Unlike <see cref="ProbeContainerHttpAsync"/>, this method
    /// does not shell out to <c>podman exec</c> and does not require any
    /// binary (<c>wget</c>, <c>curl</c>) to be present inside the workload
    /// image. Any base image — Alpine, distroless, or a custom BYOI image
    /// — can be probed without modification.
    /// </para>
    /// <para>
    /// The implementation inspects the container's network settings to find
    /// its host-routable IP address, rewrites the URL's host accordingly, and
    /// issues the probe from the dispatcher process's own HTTP client. The
    /// dispatcher process is co-located with the container runtime on the
    /// host, so it can reach containers on any bridge network managed by
    /// that runtime.
    /// </para>
    /// <para>
    /// The <paramref name="url"/> is interpreted as an in-container URL
    /// (typically <c>http://localhost:{port}/…</c>); the host part is
    /// replaced by the resolved container IP before the GET is issued.
    /// This keeps the caller's URL construction logic identical to what
    /// it was for <see cref="ProbeContainerHttpAsync"/>.
    /// </para>
    /// <para>
    /// Two reachability scenarios are supported:
    /// <list type="bullet">
    ///   <item>
    ///     <b>Dispatcher / dual-homed worker</b> — the host-side HTTP GET
    ///     is issued directly from the <see cref="ProcessContainerRuntime"/>
    ///     implementation. The container's network bridge is reachable from
    ///     the host without additional hops.
    ///   </item>
    ///   <item>
    ///     <b>Worker on <c>spring-net</c> only</b> — <see cref="DispatcherClientContainerRuntime"/>
    ///     forwards the probe request to the dispatcher over
    ///     <c>POST /v1/containers/{id}/probe-from-host</c>. The dispatcher,
    ///     running on the host, performs the same host-side HTTP GET.
    ///   </item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="containerId">Identifier of the target container.</param>
    /// <param name="url">
    /// In-container URL to probe (e.g. <c>http://localhost:8999/.well-known/agent.json</c>).
    /// The host portion is replaced by the container's inspected host-routable
    /// IP before the GET is issued.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// <c>true</c> when the endpoint answered 2xx; <c>false</c> on any
    /// non-2xx, network error, container-inspect failure, or unknown
    /// container. Callers that need to distinguish those cases should fall
    /// back to inspect / logs.
    /// </returns>
    Task<bool> ProbeHttpFromHostAsync(string containerId, string url, CancellationToken ct = default);

    /// <summary>
    /// Probes an HTTP endpoint by spawning a throwaway probe container on the
    /// named bridge network and resolving the URL via that network's DNS.
    /// Used when the target container is distroless and therefore cannot host
    /// the <c>podman exec wget</c> pattern <see cref="ProbeContainerHttpAsync"/>
    /// relies on — the canonical case is the upstream <c>daprio/daprd</c>
    /// sidecar image (no shell, no wget).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors the <c>wait_sidecar_ready</c> helper in
    /// <c>deployment/deploy.sh</c>: the dispatcher runs
    /// <c>&lt;runtime&gt; run --rm --network &lt;network&gt; &lt;probeImage&gt; …</c>
    /// with a short per-attempt deadline so a real outage still surfaces via
    /// the caller's polling loop. The probe container is removed on exit.
    /// </para>
    /// <para>
    /// The dispatcher does not pre-pull <paramref name="probeImage"/>; the
    /// underlying runtime auto-pulls on first use, after which subsequent
    /// probes are sub-second. Operators in air-gapped environments should
    /// pre-load the image (or override it via configuration) so the first
    /// probe does not pay an unbounded registry round-trip.
    /// </para>
    /// </remarks>
    /// <param name="probeImage">Container image carrying a curl-or-equivalent binary (defaults to <c>docker.io/curlimages/curl:latest</c> at the call site).</param>
    /// <param name="network">Bridge network the probe container attaches to. Must already exist; the probe target's hostname must resolve on this network.</param>
    /// <param name="url">URL to probe (e.g. <c>http://my-sidecar:3500/v1.0/healthz/outbound</c>).</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// <c>true</c> when the endpoint answered 2xx; <c>false</c> on any
    /// non-2xx, DNS / connection error, or probe-container failure.
    /// </returns>
    Task<bool> ProbeHttpFromTransientContainerAsync(
        string probeImage,
        string network,
        string url,
        CancellationToken ct = default);

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
    /// the second half of issue #1160 — the readiness probe was already
    /// dispatched through <see cref="ProbeContainerHttpAsync"/>; this method
    /// covers the actual JSON-RPC <c>message/send</c> roundtrip the A2A SDK
    /// makes after readiness. Workers wire an
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
    /// agent-base and the spring-voyage-agent-dapr image both ship it.
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
/// <param name="ContextWorkspace">
/// Optional per-invocation context workspace materialised at
/// <c>/spring/context/</c> inside the container (D1 spec § 2.2.2 — D3a).
/// Carries structured bootstrap files (e.g. <c>agent-definition.yaml</c>,
/// <c>tenant-config.json</c>) the agent SDK reads during
/// <c>initialize(context)</c>. When non-null the dispatcher writes the files
/// into a fresh per-invocation directory alongside the main workspace and
/// bind-mounts it at <see cref="ContainerWorkspace.MountPath"/> (typically
/// <c>/spring/context/</c>). Lifecycle (create / cleanup) mirrors
/// <see cref="Workspace"/>.
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
    ContainerWorkspace? Workspace = null,
    string? ContainerName = null,
    ContainerWorkspace? ContextWorkspace = null);

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