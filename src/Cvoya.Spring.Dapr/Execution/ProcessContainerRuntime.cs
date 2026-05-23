// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Base container runtime that shells out to a CLI binary (podman or docker) via <see cref="Process"/>.
/// </summary>
public class ProcessContainerRuntime(
    string binaryName,
    IOptions<ContainerRuntimeOptions> options,
    ILoggerFactory loggerFactory) : IContainerRuntime
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ProcessContainerRuntime>();
    private readonly ContainerRuntimeOptions _options = options.Value;

    /// <summary>
    /// Pulls a container image, short-circuiting to a no-op when the image
    /// is already in the local container store. The local-store probe runs
    /// as <c>&lt;binary&gt; image inspect &lt;image&gt;</c>; only on a miss
    /// does the method shell out to <c>&lt;binary&gt; pull &lt;image&gt;</c>.
    /// </summary>
    /// <param name="image">The fully-qualified container image reference.</param>
    /// <param name="timeout">Maximum wall-clock time the pull is allowed to run.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <remarks>
    /// The cache pre-check is what <c>--policy missing</c> was originally
    /// expressing on the pull (#1676), but that flag had to be removed
    /// (#1698) because <c>podman pull --policy missing</c> exits non-zero
    /// on podman 4.9.x once the image is already in the store, which the
    /// dispatcher surfaced as a 502 from <c>POST /v1/images/pull</c>.
    /// <c>image inspect</c> is a strictly-local lookup on both podman and
    /// docker — it never contacts the registry — so the same "use the
    /// local copy when present, round-trip only when missing" outcome holds
    /// for private / unreachable registries without depending on the
    /// runtime's pull-policy semantics.
    /// </remarks>
    public async Task PullImageAsync(string image, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var (inspectExit, _, _) = await RunProcessAsync(
                binaryName, BuildImageInspectArguments(image), timeoutCts.Token);

            if (inspectExit == 0)
            {
                _logger.LogInformation(
                    "Image {Image} already present in {Binary} local store; skipping pull",
                    image, binaryName);
                return;
            }

            _logger.LogInformation(
                "Pulling image {Image} using {Binary}", image, binaryName);

            var (exitCode, _, stderr) = await RunProcessAsync(
                binaryName, BuildPullArguments(image), timeoutCts.Token);

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to pull image {image}. Exit code: {exitCode}. Stderr: {stderr}");
            }

            _logger.LogInformation("Pulled image {Image}", image);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Pull of image {Image} timed out after {Timeout}", image, timeout);
            throw new TimeoutException($"Pull of image {image} exceeded timeout of {timeout}.");
        }
    }

    /// <summary>
    /// Launches a container using the configured CLI binary and waits for it to complete.
    /// </summary>
    /// <param name="config">The container configuration.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The result of the container execution.</returns>
    public async Task<ContainerResult> RunAsync(ContainerConfig config, CancellationToken ct = default)
    {
        var containerName = $"spring-exec-{Guid.NewGuid():N}";
        var arguments = BuildRunArguments(config, containerName);

        _logger.LogInformation(
            "Starting container {ContainerName} with image {Image} using {Binary}",
            containerName, config.Image, binaryName);

        var timeout = config.Timeout ?? _options.DefaultTimeout;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var (exitCode, stdout, stderr) = await RunProcessAsync(
                binaryName, arguments, timeoutCts.Token);

            _logger.LogInformation(
                "Container {ContainerName} exited with code {ExitCode}",
                containerName, exitCode);

            return new ContainerResult(containerName, exitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout expired — stop the container.
            _logger.LogWarning("Container {ContainerName} timed out after {Timeout}", containerName, timeout);
            await StopAsync(containerName, CancellationToken.None);
            throw new TimeoutException($"Container {containerName} exceeded timeout of {timeout}.");
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — stop the container.
            _logger.LogWarning("Container {ContainerName} was cancelled", containerName);
            await StopAsync(containerName, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Launches a container in detached mode, returning immediately with the container identifier.
    /// </summary>
    /// <param name="config">The container configuration.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The identifier of the started container.</returns>
    public async Task<string> StartAsync(ContainerConfig config, CancellationToken ct = default)
    {
        // Caller-provided name wins (the lifecycle uses this so the daprd
        // sidecar can dial the agent container by DNS via
        // `--app-channel-address`; see DaprSidecarConfig.AppChannelAddress
        // and ContainerLifecycleManager.LaunchWithSidecarDetachedAsync).
        // Fall back to a fresh `spring-persistent-<guid>` so legacy callers
        // that don't care about a stable name keep working.
        var containerName = string.IsNullOrWhiteSpace(config.ContainerName)
            ? $"spring-persistent-{Guid.NewGuid():N}"
            : config.ContainerName;
        var arguments = BuildStartArguments(config, containerName);

        _logger.LogInformation(
            "Starting detached container {ContainerName} with image {Image} using {Binary}",
            containerName, config.Image, binaryName);

        var (exitCode, stdout, stderr) = await RunProcessAsync(binaryName, arguments, ct);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to start container {containerName}. Exit code: {exitCode}. Stderr: {stderr}");
        }

        _logger.LogInformation("Detached container {ContainerName} started", containerName);
        return containerName;
    }

    /// <summary>
    /// Stops and removes a container by its identifier.
    /// </summary>
    /// <param name="containerId">The identifier of the container to stop.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public async Task StopAsync(string containerId, CancellationToken ct = default)
    {
        _logger.LogInformation("Stopping container {ContainerId} using {Binary}", containerId, binaryName);

        try
        {
            await RunProcessAsync(binaryName, ["stop", containerId], ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop container {ContainerId}", containerId);
        }

        try
        {
            await RunProcessAsync(binaryName, ["rm", "-f", containerId], ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove container {ContainerId}", containerId);
        }
    }

    /// <summary>
    /// Reads the last <paramref name="tail"/> lines of a container's combined
    /// stdout+stderr. Shells out to <c>&lt;binary&gt; logs --tail &lt;N&gt;</c> and
    /// surfaces the captured output verbatim. Used by the persistent-agent
    /// CLI surface (<c>spring agent logs</c>, #396).
    /// </summary>
    /// <param name="containerId">The identifier of the container to read.</param>
    /// <param name="tail">Maximum number of log lines to return.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public async Task<string> GetLogsAsync(string containerId, int tail = 200, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("Container id is required.", nameof(containerId));
        }

        // Clamp the tail value so a caller can't drive the CLI into an
        // unbounded read. `docker logs --tail 0` (or `all`) is valid but we
        // require a positive value here because the API endpoint exposes this
        // directly and we want the contract stable.
        var effectiveTail = tail <= 0 ? 200 : tail;

        string[] arguments =
        [
            "logs",
            "--tail",
            effectiveTail.ToString(System.Globalization.CultureInfo.InvariantCulture),
            containerId,
        ];

        _logger.LogDebug(
            "Reading last {Tail} log lines from container {ContainerId} using {Binary}",
            effectiveTail, containerId, binaryName);

        var (exitCode, stdout, stderr) = await RunProcessAsync(binaryName, arguments, ct);

        if (exitCode != 0)
        {
            // The CLI sends diagnostic text to stderr when the container id is
            // unknown. Surface a meaningful exception so the API endpoint can
            // translate into a 404.
            throw new InvalidOperationException(
                $"Failed to read logs for container {containerId}. Exit code: {exitCode}. Stderr: {stderr}");
        }

        // Docker and podman write container stdout to the process stdout and
        // container stderr to the process stderr — combine them so the single
        // return string matches what an operator sees on a local `docker logs`.
        if (string.IsNullOrEmpty(stderr))
        {
            return stdout;
        }

        if (string.IsNullOrEmpty(stdout))
        {
            return stderr;
        }

        return stdout + stderr;
    }

    /// <inheritdoc />
    public async Task<ContainerHealth> GetHealthAsync(string containerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        // Ask the runtime for the HEALTHCHECK state from container metadata.
        // Both podman and docker support this Go template expression:
        //   - "healthy"   — HEALTHCHECK passed on the most recent run
        //   - "unhealthy" — HEALTHCHECK failed
        //   - "starting"  — container is still in the initial grace period
        //   - ""          — no HEALTHCHECK instruction in the image
        //
        // A non-zero inspect exit means the container is unknown to the runtime;
        // we surface that as InvalidOperationException so the API layer can 404.
        // The `{{if .State.Health}}…{{end}}` guard handles the no-HEALTHCHECK
        // case: when the image declares no HEALTHCHECK instruction, `Health`
        // is the Go zero-value (a null pointer); the unguarded
        // `{{.State.Health.Status}}` template would crash `podman inspect`
        // with a non-zero exit and surface as "container is not known to the
        // runtime" even though it exists and is running. With the guard the
        // template emits the empty string for no-healthcheck containers,
        // which the post-process branch below already maps to "healthy by
        // convention".
        var (exitCode, stdout, stderr) = await RunProcessAsync(
            binaryName,
            [
                "inspect",
                "--format",
                "{{if .State.Health}}{{.State.Health.Status}}{{end}}",
                containerId,
            ],
            ct);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Container '{containerId}' is not known to the runtime. "
                + $"Exit code: {exitCode}. Stderr: {stderr}");
        }

        var status = stdout.Trim();

        // Empty status: image declared no HEALTHCHECK instruction. Treat as
        // healthy-by-convention so health-naive images don't show as unhealthy.
        if (string.IsNullOrWhiteSpace(status))
        {
            return new ContainerHealth(Healthy: true, Detail: "no healthcheck declared");
        }

        // "starting" means the container is still in its initial grace period
        // before the first HEALTHCHECK probe fires. Treat as unhealthy for now
        // so callers can distinguish "not yet ready" from "confirmed healthy".
        var healthy = string.Equals(status, "healthy", StringComparison.OrdinalIgnoreCase);
        return new ContainerHealth(Healthy: healthy, Detail: status);
    }

    /// <inheritdoc />
    public async Task CreateNetworkAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _logger.LogInformation(
            "Creating container network {NetworkName} using {Binary}", name, binaryName);

        var (exitCode, _, stderr) = await RunProcessAsync(
            binaryName, ["network", "create", name], ct);

        if (exitCode == 0)
        {
            return;
        }

        // Idempotency: both podman and docker emit "already exists" on stderr
        // when the network is already present. Match that and treat as success
        // so the lifecycle manager can call this once per boot without first
        // doing a `network inspect` round-trip. Substring match (rather than
        // an exact regex) because both runtimes pad the message slightly
        // differently across versions ("network with name X already exists",
        // "network X already exists", "Error response from daemon: network
        // with name X already exists").
        if (stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Container network {NetworkName} already exists; treating create as no-op", name);
            return;
        }

        throw new InvalidOperationException(
            $"Failed to create container network {name}. Exit code: {exitCode}. Stderr: {stderr}");
    }

    /// <inheritdoc />
    public async Task<bool> ProbeContainerHttpAsync(string containerId, string url, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        // curl exit codes worth knowing:
        //   0 — request succeeded (2xx, with --fail)
        //   non-zero — DNS failure, connection refused, 4xx/5xx, missing
        //   binary, container gone. Every non-zero collapses to "false"
        //   because callers (sidecar / agent health polling) treat the
        //   result as a single bit. The shell-out is intentionally small —
        //   --silent + --output /dev/null + --max-time bounds the per-call
        //   cost without widening the dispatcher's RCE surface area.
        string[] args =
        [
            "exec",
            containerId,
            "curl",
            "--silent",
            "--show-error",
            "--fail",
            "--max-time", "10",
            "--output", "/dev/null",
            url,
        ];

        try
        {
            var (exitCode, _, _) = await RunProcessAsync(binaryName, args, ct);
            return exitCode == 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Local timeout on the underlying RunProcessAsync — propagate
            // false so the polling loop just waits and tries again.
            return false;
        }
        catch (Exception ex)
        {
            // curl missing / container exited / runtime crashed — collapse
            // to false so the caller's polling loop is the only place that
            // owns timeout + retry semantics.
            _logger.LogDebug(
                ex,
                "Probe of {Url} inside container {ContainerId} failed: {Message}",
                url, containerId, ex.Message);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ContainerResult> WaitForExitAsync(string containerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        // `podman wait <id>` blocks until the container exits and prints the
        // exit code on stdout. This is the event-driven replacement for the
        // poll-on-`inspect` pattern — one process per wait, no spin loop.
        var (waitExitCode, waitStdout, waitStderr) = await RunProcessAsync(
            binaryName, ["wait", containerId], ct);

        if (waitExitCode != 0)
        {
            // Most common failure: container id unknown (returns non-zero
            // with "no container with name or ID …" on stderr). Surface as
            // InvalidOperationException so the API layer maps to 404 — same
            // contract as RemoveAsync / GetLogsAsync.
            throw new InvalidOperationException(
                $"podman wait {containerId} failed with exit code {waitExitCode}. Stderr: {waitStderr.Trim()}");
        }

        var exitCode = int.TryParse(
            waitStdout.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed) ? parsed : -1;

        // Now collect the captured stdout/stderr. `podman logs` returns
        // stdout on its own stdout and stderr on its own stderr — same
        // separation we'd have with `Process.Start` of a real foreground run.
        string stdout = string.Empty;
        string stderr = string.Empty;
        try
        {
            (_, stdout, stderr) = await RunProcessAsync(
                binaryName, ["logs", containerId], CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Container may have been auto-removed by `--rm` between wait and
            // logs; the exit code is what callers really want, so log + ignore.
            _logger.LogDebug(
                ex,
                "podman logs {ContainerId} failed after wait; returning exit code only",
                containerId);
        }

        _logger.LogInformation(
            "Container {ContainerId} exited with code {ExitCode}", containerId, exitCode);

        return new ContainerResult(containerId, exitCode, stdout, stderr);
    }

    /// <inheritdoc />
    public async Task<ContainerHttpResponse> SendHttpJsonAsync(
        string containerId,
        string url,
        byte[] body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(body);

        // curl reads the body from stdin via `--data-binary @-` reliably on
        // both BusyBox and GNU coreutils — unlike `wget --post-file=/dev/stdin`,
        // which GNU wget on Debian rejects with "Illegal seek" because the
        // exec pipe is not seekable. `--data-binary` (vs. `-d`) preserves
        // bytes verbatim — no @ / & interpretation, no newline stripping.
        // `-sS` silences progress but keeps real errors on stderr.
        //
        // We deliberately do NOT pass `-f` (fail-on-error). The agent's
        // JSON-RPC handler may legitimately return a non-2xx with a body
        // (A2A error envelope, validation error, agent-side 5xx) and the
        // caller — the .NET A2AClient — needs to see that status + body
        // round-tripped intact rather than collapsed into a 502 with no
        // body. We use `--write-out` to append a sentinel line carrying
        // the actual HTTP status code; everything before the sentinel is
        // the response body. The sentinel is unguessable enough that
        // collisions with response bodies are not a real concern. The
        // header argument is a single argv entry (no whitespace splitting
        // via ArgumentList) so the Content-Type value passes through
        // verbatim.
        const string StatusSentinel = "\n___SV_HTTP_STATUS___:";
        string[] args =
        [
            "exec",
            "-i",
            containerId,
            "curl",
            "-sS",
            "-X", "POST",
            "-H", "Content-Type: application/json",
            "--data-binary", "@-",
            "--write-out", StatusSentinel + "%{http_code}",
            url,
        ];

        try
        {
            var (exitCode, stdout, stderr) = await RunProcessWithStdinAsync(
                binaryName, args, body, ct);

            if (exitCode == 0)
            {
                var sentinelIdx = stdout.LastIndexOf(StatusSentinel, StringComparison.Ordinal);
                if (sentinelIdx >= 0)
                {
                    var bodyText = stdout[..sentinelIdx];
                    var statusText = stdout[(sentinelIdx + StatusSentinel.Length)..].Trim();
                    if (int.TryParse(statusText, out var parsedStatus) && parsedStatus > 0)
                    {
                        // Diagnostic: dump body (capped at 32 KB) so wire-shape
                        // mismatches and agent-side errors are visible in the
                        // dispatcher log without raising the global log level.
                        var snippet = bodyText.Length <= 32768
                            ? bodyText
                            : bodyText[..32768] + "…(truncated)";
                        _logger.LogInformation(
                            "A2A response from {ContainerId} ({Url}) status={Status} body={Body}",
                            containerId, url, parsedStatus, snippet);
                        return new ContainerHttpResponse(
                            StatusCode: parsedStatus,
                            Body: System.Text.Encoding.UTF8.GetBytes(bodyText));
                    }
                    _logger.LogWarning(
                        "POST to {Url} inside container {ContainerId} produced an unparseable status sentinel: {Sentinel}",
                        url, containerId, statusText);
                    return new ContainerHttpResponse(StatusCode: 502, Body: []);
                }
                _logger.LogWarning(
                    "POST to {Url} inside container {ContainerId} produced no status sentinel; treating as transport failure (stderr: {Stderr})",
                    url, containerId, stderr);
                return new ContainerHttpResponse(StatusCode: 502, Body: []);
            }

            // Curl itself failed (network unreachable, missing binary,
            // connection refused, etc.). Surface as 502 — the worker's
            // retry loop owns the next move. Log at Warning so the failure
            // mode is diagnosable without raising the global log level.
            _logger.LogWarning(
                "POST to {Url} inside container {ContainerId} exited {ExitCode} via {Binary} curl: {Stderr}",
                url, containerId, exitCode, binaryName, stderr);
            return new ContainerHttpResponse(StatusCode: 502, Body: []);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Local timeout — surface as 502 so the worker's retry loop can
            // own the next attempt.
            return new ContainerHttpResponse(StatusCode: 502, Body: []);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "POST to {Url} inside container {ContainerId} failed: {Message}",
                url, containerId, ex.Message);
            return new ContainerHttpResponse(StatusCode: 502, Body: []);
        }
    }

    /// <inheritdoc />
    public async Task EnsureVolumeAsync(string volumeName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        _logger.LogInformation(
            "Ensuring volume {VolumeName} exists using {Binary}", volumeName, binaryName);

        var (exitCode, _, stderr) = await RunProcessAsync(
            binaryName, ["volume", "create", volumeName], ct);

        if (exitCode == 0)
        {
            return;
        }

        // Idempotency: both podman and docker emit "already exists" on stderr
        // when the volume is already present.
        if (stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Volume {VolumeName} already exists; treating create as no-op", volumeName);
            return;
        }

        throw new InvalidOperationException(
            $"Failed to create volume {volumeName}. Exit code: {exitCode}. Stderr: {stderr}");
    }

    /// <inheritdoc />
    public async Task RemoveVolumeAsync(string volumeName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        _logger.LogInformation(
            "Removing volume {VolumeName} using {Binary}", volumeName, binaryName);

        var (exitCode, _, stderr) = await RunProcessAsync(
            binaryName, ["volume", "rm", volumeName], ct);

        if (exitCode == 0)
        {
            return;
        }

        // Idempotency: volume does not exist is success.
        if (stderr.Contains("no such volume", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Volume {VolumeName} did not exist; treating remove as no-op", volumeName);
            return;
        }

        // Volume still in use — warn and return instead of throwing so the
        // registry entry can still be reclaimed. The volume will be cleaned
        // up when the last container using it stops, or via operator tooling.
        if (stderr.Contains("in use", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("volume is in use", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Volume {VolumeName} is still in use; deferring removal", volumeName);
            return;
        }

        throw new InvalidOperationException(
            $"Failed to remove volume {volumeName}. Exit code: {exitCode}. Stderr: {stderr}");
    }

    /// <summary>
    /// Resolves a named volume to its host-side mount point by shelling out to
    /// <c>volume inspect --format {{.Mountpoint}}</c>. Returns <c>null</c> when
    /// the volume does not exist.
    /// </summary>
    /// <remarks>
    /// Used only by <see cref="GetVolumeMetricsAsync"/>, which needs a path
    /// to hand to <c>du</c>. The path is meaningful only when the dispatcher
    /// shares a filesystem with the runtime's volume storage — but volume
    /// content is no longer written from the dispatcher under ADR-0055 (the
    /// agent-sidecar pulls it from the worker), so this resolver is only
    /// reached on the metrics path.
    /// </remarks>
    private async Task<string?> ResolveVolumeMountpointAsync(string volumeName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        // Use `volume inspect --format` to extract the Mountpoint field
        // without parsing full JSON.
        var (inspectExit, mountpoint, _) = await RunProcessAsync(
            binaryName,
            ["volume", "inspect", "--format", "{{.Mountpoint}}", volumeName],
            ct);

        if (inspectExit != 0)
        {
            // Volume does not exist or inspect failed — return null per the contract.
            return null;
        }

        mountpoint = mountpoint.Trim();
        return string.IsNullOrEmpty(mountpoint) ? null : mountpoint;
    }

    /// <inheritdoc />
    public async Task<VolumeMetrics?> GetVolumeMetricsAsync(string volumeName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        // Content is never read; we only need the filesystem path to call `du`.
        var mountpoint = await ResolveVolumeMountpointAsync(volumeName, ct);
        if (mountpoint is null)
        {
            return null;
        }

        // `du -sb` gives bytes (GNU coreutils); `du -sk` × 1024 is a POSIX
        // fallback. We accept null SizeBytes when du is unavailable on the host.
        long? sizeBytes = null;
        try
        {
            var (duExit, duOut, _) = await RunProcessAsync(
                "du", ["-sb", mountpoint], ct);

            if (duExit == 0)
            {
                var parts = duOut.Split('\t', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && long.TryParse(parts[0].Trim(), out var bytes))
                {
                    sizeBytes = bytes;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not determine size for volume {VolumeName}", volumeName);
        }

        return new VolumeMetrics(sizeBytes, LastWrite: null);
    }

    /// <inheritdoc />
    public async Task RemoveNetworkAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _logger.LogInformation(
            "Removing container network {NetworkName} using {Binary}", name, binaryName);

        var (exitCode, _, stderr) = await RunProcessAsync(
            binaryName, ["network", "rm", name], ct);

        if (exitCode == 0)
        {
            return;
        }

        // Idempotency mirrors CreateNetworkAsync: a missing network on remove
        // is success. Both runtimes report "no such network" (docker) or
        // "network not found" / "network <name> not found" (podman).
        if (stderr.Contains("no such network", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Container network {NetworkName} did not exist; treating remove as no-op", name);
            return;
        }

        throw new InvalidOperationException(
            $"Failed to remove container network {name}. Exit code: {exitCode}. Stderr: {stderr}");
    }

    /// <summary>
    /// Builds the argv vector for the container run command.
    /// </summary>
    /// <remarks>
    /// Returns one argv entry per token (no shell-style escaping needed) so
    /// <see cref="ProcessStartInfo.ArgumentList"/> can pass each value
    /// verbatim to the container CLI. Concatenating with spaces and using
    /// <see cref="ProcessStartInfo.Arguments"/> would split values on
    /// whitespace and break any env-var, label, or volume-mount value that
    /// contains a space (the assembled system prompt for a delegated agent
    /// is the most common offender — see the bug log alongside this fix).
    /// </remarks>
    /// <param name="config">The container configuration.</param>
    /// <param name="containerName">The unique container name.</param>
    /// <returns>The argv list for the run command.</returns>
    internal static IReadOnlyList<string> BuildRunArguments(ContainerConfig config, string containerName)
    {
        var args = new List<string> { "run", "--rm", "--name", containerName };
        AppendCommonArguments(args, config);
        return args;
    }

    /// <summary>
    /// Builds the argv vector for a detached container start command. Same
    /// quoting-safety story as <see cref="BuildRunArguments"/>.
    /// </summary>
    internal static IReadOnlyList<string> BuildStartArguments(ContainerConfig config, string containerName)
    {
        var args = new List<string> { "run", "-d", "--name", containerName };
        AppendCommonArguments(args, config);
        return args;
    }

    /// <summary>
    /// Builds the argv vector for an image-pull command. The locally-cached
    /// short-circuit lives on the caller side as an explicit
    /// <see cref="BuildImageInspectArguments(string)"/> probe rather than as
    /// a <c>--policy missing</c> flag on the pull, because the latter exits
    /// non-zero on podman 4.9.x for already-cached images and the dispatcher
    /// surfaces the failure as a 502 from <c>POST /v1/images/pull</c>
    /// (regression from #1682, fixed in #1698).
    /// </summary>
    internal static IReadOnlyList<string> BuildPullArguments(string image)
        => ["pull", image];

    /// <summary>
    /// Builds the argv vector for the local-store image probe used by
    /// <see cref="PullImageAsync(string, TimeSpan, CancellationToken)"/>.
    /// Both podman and docker treat <c>image inspect &lt;image&gt;</c> as a
    /// strictly-local lookup that never contacts the registry: exit 0 when
    /// the image is in the local store and exit 1 otherwise. That is what
    /// drives the pull-time short-circuit (#1676 / #1698).
    /// </summary>
    internal static IReadOnlyList<string> BuildImageInspectArguments(string image)
        => ["image", "inspect", image];

    /// <summary>
    /// Appends the option / image / command portion shared by run and
    /// detached-start invocations to <paramref name="args"/>. Each value
    /// is added as a single argv entry — never split, never re-parsed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ContainerConfig.Command"/> is now a list, so each entry
    /// becomes one argv token verbatim — no whitespace splitting and no
    /// fragility for tokens that legitimately contain spaces (#1063 / #1093).
    /// Producers that previously joined tokens with single spaces
    /// (<c>DaprSidecarManager</c>, <c>RunContainerProbeActivity</c>) have
    /// been updated to pass the list directly.
    /// </para>
    /// </remarks>
    private static void AppendCommonArguments(List<string> args, ContainerConfig config)
    {
        if (config.NetworkName is not null)
        {
            args.Add("--network");
            args.Add(config.NetworkName);
        }

        // Additional networks ride as repeated `--network` flags. Both podman
        // and docker (>= 20.10) accept the option more than once; the dispatcher
        // attaches the container to every named network at create time. Used to
        // dual-attach a workflow / unit container to the per-tenant bridge on
        // top of the per-app spring-net-<guid> sidecar bridge — see ADR 0028
        // and issue #1166. Empty / null is the no-op default.
        if (config.AdditionalNetworks is { Count: > 0 } extraNetworks)
        {
            foreach (var network in extraNetworks)
            {
                if (string.IsNullOrWhiteSpace(network))
                {
                    continue;
                }
                args.Add("--network");
                args.Add(network);
            }
        }

        if (config.Labels is not null)
        {
            foreach (var (key, value) in config.Labels)
            {
                args.Add("--label");
                args.Add($"{key}={value}");
            }
        }

        if (config.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in config.EnvironmentVariables)
            {
                args.Add("-e");
                args.Add($"{key}={value}");
            }
        }

        if (config.VolumeMounts is not null)
        {
            foreach (var mount in config.VolumeMounts)
            {
                args.Add("-v");
                args.Add(mount);
            }
        }

        if (config.ExtraHosts is not null)
        {
            foreach (var host in config.ExtraHosts)
            {
                args.Add($"--add-host={host}");
            }
        }

        if (!string.IsNullOrEmpty(config.WorkingDirectory))
        {
            args.Add("-w");
            args.Add(config.WorkingDirectory);
        }

        // #1686: --entrypoint must precede the image positional entry so
        // podman / docker apply it as the override for the launched
        // container. Used by RunContainerProbeActivity to bypass the OSS
        // agent images' inherited A2A-bridge ENTRYPOINT and run a one-shot
        // tool invocation (e.g. `claude --version`) directly.
        if (!string.IsNullOrWhiteSpace(config.Entrypoint))
        {
            args.Add("--entrypoint");
            args.Add(config.Entrypoint);
        }

        args.Add(config.Image);

        if (config.Command is { Count: > 0 } command)
        {
            args.AddRange(command);
        }
    }

    /// <summary>
    /// Like <see cref="RunProcessAsync"/> but pipes <paramref name="stdin"/>
    /// to the child process's standard input. Used by
    /// <see cref="SendHttpJsonAsync"/> to stream a JSON request body into
    /// <c>podman exec -i ... wget --post-file=/dev/stdin</c> without going
    /// through argv (which has size limits and shell-escape concerns).
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessWithStdinAsync(
        string fileName, IEnumerable<string> arguments, byte[] stdin, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        StartProcessOrTranslate(process, fileName);

        // Write the body and close stdin so the child sees EOF. Closing the
        // BaseStream is the only reliable cross-platform way to propagate
        // EOF to the child's stdin under .NET's process model.
        await process.StandardInput.BaseStream.WriteAsync(stdin, ct);
        await process.StandardInput.BaseStream.FlushAsync(ct);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, IEnumerable<string> arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // ArgumentList passes each entry as a separate argv element directly
        // to posix_spawn / CreateProcess, so values containing whitespace,
        // '=', quotes, or other shell-meaningful characters travel through
        // unchanged. The string-based ProcessStartInfo.Arguments path would
        // re-split on whitespace and break any env-var / label / volume
        // value containing a space.
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        StartProcessOrTranslate(process, fileName);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Calls <see cref="Process.Start()"/> and, on failure, rewrites the
    /// exception to name the dispatcher's working directory when the cwd
    /// itself is the cause. See issue #1674: on macOS and Linux,
    /// <c>posix_spawn</c> returns an ENOENT-flavoured error when the
    /// parent's <c>getcwd()</c> no longer resolves — typical after the
    /// operator removes the git worktree the dispatcher was launched
    /// from — and the BCL surfaces that as
    /// <see cref="FileNotFoundException"/>. Without this rewrite the log
    /// reads "Unable to find the specified file" with no hint that the
    /// cwd is the problem, so operators spend an hour chasing a missing
    /// <c>podman</c> binary.
    /// </summary>
    internal static void StartProcessOrTranslate(Process process, string fileName)
    {
        try
        {
            process.Start();
        }
        catch (Exception ex)
            when (ex is FileNotFoundException
                or DirectoryNotFoundException
                or Win32Exception
                or InvalidOperationException)
        {
            throw TranslateProcessStartFailure(ex, fileName);
        }
    }

    /// <summary>
    /// Inspects the current working directory and, when it is unreachable,
    /// wraps <paramref name="original"/> in an
    /// <see cref="InvalidOperationException"/> that names the cwd problem
    /// explicitly. Returns <paramref name="original"/> unchanged when the
    /// cwd is healthy — the original exception is honest in that case.
    /// </summary>
    /// <remarks>
    /// Extracted as <c>internal static</c> so the .Tests project can
    /// exercise the decision logic without spawning a real process. The
    /// cwd probe is an overridable delegate so tests never have to
    /// <c>rmdir</c> the test runner's cwd mid-run (that would race every
    /// parallel test in the same assembly and is platform-variable).
    /// </remarks>
    internal static Exception TranslateProcessStartFailure(Exception original, string fileName)
        => TranslateProcessStartFailure(original, fileName, DefaultCwdProbe);

    /// <summary>
    /// Same as <see cref="TranslateProcessStartFailure(Exception, string)"/>
    /// but with an injectable cwd probe for unit-testability. Production
    /// callers go through the two-argument overload; tests synthesise
    /// deleted-cwd / throwing-<c>getcwd</c> scenarios by passing a fake
    /// probe here.
    /// </summary>
    internal static Exception TranslateProcessStartFailure(
        Exception original,
        string fileName,
        Func<CwdProbeResult> probe)
    {
        var (cwd, cwdError) = probe();

        if (cwdError is null)
        {
            return original;
        }

        var cwdDescription = string.IsNullOrEmpty(cwd) ? "unknown" : $"'{cwd}'";
        var message =
            $"Failed to start '{fileName}' because the dispatcher's working directory ({cwdDescription}) is unreachable: {cwdError}. "
            + "Restart the dispatcher from a valid directory (e.g. `./eng/deploy/spring-voyage-host.sh restart`). "
            + "See https://github.com/cvoya-com/spring-voyage/issues/1674.";
        return new InvalidOperationException(message, original);
    }

    /// <summary>
    /// Default cwd probe used by <see cref="TranslateProcessStartFailure(Exception, string)"/>.
    /// Returns <c>(cwd, null)</c> when <see cref="Directory.GetCurrentDirectory"/>
    /// succeeds and the resolved path still exists, <c>(cwd, error)</c>
    /// otherwise. Never throws.
    /// </summary>
    internal static CwdProbeResult DefaultCwdProbe()
    {
        string cwd;
        try
        {
            cwd = Directory.GetCurrentDirectory();
        }
        catch (Exception ex)
        {
            return new CwdProbeResult(
                Cwd: null,
                Error: $"Directory.GetCurrentDirectory() threw {ex.GetType().Name}: {ex.Message}");
        }

        if (!Directory.Exists(cwd))
        {
            return new CwdProbeResult(
                Cwd: cwd,
                Error: "cwd path no longer exists on disk (inode unlinked?)");
        }

        return new CwdProbeResult(Cwd: cwd, Error: null);
    }

    /// <summary>
    /// Result of a cwd probe: the resolved path (when available) and an
    /// optional failure reason. <see cref="Error"/> is <c>null</c> on
    /// success and carries a human-readable diagnosis on failure. Kept
    /// <c>internal</c> — it's an implementation detail of the translator,
    /// not part of the runtime's public API.
    /// </summary>
    internal readonly record struct CwdProbeResult(string? Cwd, string? Error);
}
