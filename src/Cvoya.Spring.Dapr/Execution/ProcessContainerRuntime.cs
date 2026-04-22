// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Diagnostics;

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
    /// Pulls a container image by shelling out to <c>&lt;binary&gt; pull &lt;image&gt;</c>.
    /// </summary>
    /// <param name="image">The fully-qualified container image reference.</param>
    /// <param name="timeout">Maximum wall-clock time the pull is allowed to run.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public async Task PullImageAsync(string image, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);

        _logger.LogInformation(
            "Pulling image {Image} using {Binary}", image, binaryName);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var (exitCode, _, stderr) = await RunProcessAsync(
                binaryName, ["pull", image], timeoutCts.Token);

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
        var containerName = $"spring-persistent-{Guid.NewGuid():N}";
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

        // wget exit codes worth knowing:
        //   0 — request succeeded (2xx)
        //   1..8 — request failed (DNS / connection / non-2xx)
        // We map every non-zero into "false" because callers (sidecar health
        // polling) treat the result as a single bit. The shell-out here is
        // intentionally small: no flags beyond -q --spider so we don't
        // accidentally widen the attack surface beyond what the original
        // worker-side `podman exec ... wget` did.
        string[] args = ["exec", containerId, "wget", "-q", "--spider", url];

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
            // wget missing / container exited / runtime crashed — collapse
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
    /// Appends the option / image / command portion shared by run and
    /// detached-start invocations to <paramref name="args"/>. Each value
    /// is added as a single argv entry — never split, never re-parsed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ContainerConfig.Command"/> is split on whitespace because
    /// the previous string-based builder appended it verbatim and existing
    /// callers (most notably <c>DaprSidecarManager</c>) rely on being able
    /// to express a multi-token command (<c>./daprd --app-id … --app-port …</c>)
    /// as a single string. The split uses
    /// <see cref="StringSplitOptions.RemoveEmptyEntries"/> so trailing or
    /// duplicate spaces don't produce empty argv entries that podman would
    /// reject. This preserves backwards compatibility while still avoiding
    /// the env-var / label whitespace breakage.
    /// </para>
    /// </remarks>
    private static void AppendCommonArguments(List<string> args, ContainerConfig config)
    {
        if (config.NetworkName is not null)
        {
            args.Add("--network");
            args.Add(config.NetworkName);
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

        args.Add(config.Image);

        if (!string.IsNullOrWhiteSpace(config.Command))
        {
            args.AddRange(config.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
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

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }
}