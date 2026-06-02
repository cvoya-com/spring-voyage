// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="IContainerRuntime"/> implementation that forwards every call to
/// a remote <c>spring-dispatcher</c> service over HTTP. The worker process
/// holds no container-runtime credentials of its own — the dispatcher owns
/// the local <c>podman</c> binary and the socket it talks to.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the in-process <c>PodmanRuntime</c>/<c>DockerRuntime</c>
/// bindings that used to run inside the worker. Workers now depend only on
/// <see cref="IContainerRuntime"/>; the HTTP hop is invisible to callers such
/// as <c>A2AExecutionDispatcher</c>.
/// </para>
/// <para>
/// Auth is a single bearer token issued per-worker at deploy time; it is
/// stamped onto every outbound request and scoped to a tenant on the
/// dispatcher side. See <see cref="DispatcherClientOptions"/>.
/// </para>
/// </remarks>
public class DispatcherClientContainerRuntime(
    IHttpClientFactory httpClientFactory,
    IOptions<DispatcherClientOptions> options,
    ILoggerFactory loggerFactory) : IContainerRuntime
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DispatcherClientContainerRuntime>();
    private readonly DispatcherClientOptions _options = options.Value;

    /// <summary>Name of the HTTP client registered for the dispatcher.</summary>
    public const string HttpClientName = "spring-dispatcher";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task PullImageAsync(string image, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);

        var httpClient = CreateClient();
        var request = new DispatcherPullRequest
        {
            Image = image,
            TimeoutSeconds = (int)timeout.TotalSeconds,
        };

        _logger.LogInformation(
            "Requesting dispatcher pull for image {Image}", image);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "v1/images/pull", request, JsonOptions, timeoutCts.Token);

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var body = await SafeReadBodyAsync(response, timeoutCts.Token);

            // Round-trip the dispatcher's classification so PullImageActivity
            // can keep its existing exception-shape switch:
            //   504  → server-side timeout exceeded → TimeoutException
            //   any other non-2xx → registry/runtime refused → InvalidOperationException
            if (response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout)
            {
                throw new TimeoutException(
                    $"Dispatcher reported pull timeout for image {image}: {body}");
            }

            throw new InvalidOperationException(
                $"Dispatcher returned {(int)response.StatusCode} pulling image {image}: {body}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Local timeoutCts fired before the dispatcher answered. Caller's
            // ct is intact, so this is "we waited the full deadline locally".
            throw new TimeoutException($"Pull of image {image} exceeded timeout of {timeout}.");
        }
    }

    /// <inheritdoc />
    public async Task<ContainerResult> RunAsync(ContainerConfig config, CancellationToken ct = default)
    {
        var request = BuildRunRequest(config, detached: false);
        var response = await SendRunAsync(request, ct);

        return new ContainerResult(
            ContainerId: response.Id,
            ExitCode: response.ExitCode ?? 0,
            StandardOutput: response.StandardOutput ?? string.Empty,
            StandardError: response.StandardError ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<string> StartAsync(ContainerConfig config, CancellationToken ct = default)
    {
        var request = BuildRunRequest(config, detached: true);
        var response = await SendRunAsync(request, ct);
        return response.Id;
    }

    /// <inheritdoc />
    public async Task<string> GetLogsAsync(string containerId, int tail = 200, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var httpClient = CreateClient();
        var uri = $"v1/containers/{Uri.EscapeDataString(containerId)}/logs?tail={tail}";

        using var response = await httpClient.GetAsync(uri, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Container '{containerId}' not known to the dispatcher.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, ct);
            throw new InvalidOperationException(
                $"Dispatcher returned {(int)response.StatusCode} fetching logs for {containerId}: {body}");
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Retry delays for transient failures in <see cref="GetHealthAsync"/>.
    /// Three attempts total: first failure → 200 ms, second → 600 ms.
    /// Total added wait ≤ 800 ms — well within any caller's polling budget.
    /// </summary>
    private static readonly TimeSpan[] HealthRetryDelays = [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(600),
    ];

    /// <inheritdoc />
    public async Task<ContainerHealth> GetHealthAsync(string containerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var uri = $"v1/containers/{Uri.EscapeDataString(containerId)}/health";

        // Retry on transient 5xx and network exceptions. 404 (container not
        // found) and 503-with-body (documented "unhealthy" response) are NOT
        // transient — they carry semantic meaning and must not be retried.
        // The attempt loop is bounded by HealthRetryDelays.Length + 1.
        Exception? lastException = null;
        for (var attempt = 0; attempt <= HealthRetryDelays.Length; attempt++)
        {
            if (attempt > 0)
            {
                _logger.LogDebug(
                    "GetHealthAsync transient failure on attempt {Attempt} for container {ContainerId}; retrying in {Delay}",
                    attempt, containerId, HealthRetryDelays[attempt - 1]);
                await Task.Delay(HealthRetryDelays[attempt - 1], ct);
            }

            HttpResponseMessage response;
            try
            {
                response = await CreateClient().GetAsync(uri, ct);
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                // Network-level failure is transient — retry unless exhausted.
                if (attempt < HealthRetryDelays.Length)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Dispatcher health call for '{containerId}' failed after {attempt + 1} attempt(s): {ex.Message}", ex);
            }

            using (response)
            {
                // 404 → container not known. Definitive; do not retry.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException(
                        $"Container '{containerId}' is not known to the dispatcher.");
                }

                // 503 is the documented dispatcher response for an unhealthy
                // container (the HEALTHCHECK ran and returned "unhealthy"). That
                // is a successful transport — the payload carries the status. Do
                // NOT retry; fall through to deserialise the body.
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    // 5xx (other than 503) may be transient dispatcher restarts.
                    var statusCode = (int)response.StatusCode;
                    if (statusCode >= 500 && attempt < HealthRetryDelays.Length)
                    {
                        // Transient; retry.
                        continue;
                    }

                    var body = await SafeReadBodyAsync(response, ct);
                    throw new InvalidOperationException(
                        $"Dispatcher returned {statusCode} fetching health for {containerId}: {body}");
                }

                var parsed = await response.Content.ReadFromJsonAsync<DispatcherContainerHealthResponse>(JsonOptions, ct);
                if (parsed is null)
                {
                    throw new InvalidOperationException(
                        "Dispatcher returned an empty response body for the container health call.");
                }

                var healthy = string.Equals(parsed.Status, "healthy", StringComparison.OrdinalIgnoreCase);
                return new ContainerHealth(Healthy: healthy, Detail: parsed.Reason ?? parsed.Method);
            }
        }

        // Unreachable — all retry paths either return or throw above — but the
        // compiler cannot prove it. Rethrow the last network exception if any.
        throw lastException
            ?? new InvalidOperationException(
                $"GetHealthAsync for '{containerId}' exhausted all attempts without a conclusive response.");
    }

    /// <inheritdoc />
    public async Task<bool> ProbeContainerHttpAsync(string containerId, string url, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var httpClient = CreateClient();
        var uri = $"v1/containers/{Uri.EscapeDataString(containerId)}/probe";
        var request = new DispatcherProbeRequest { Url = url };

        using var response = await httpClient.PostAsJsonAsync(uri, request, JsonOptions, ct);

        // 404 (container unknown) collapses to "not healthy" so the polling
        // loop in DaprSidecarManager treats a vanished container as a probe
        // failure rather than a hard exception. The dispatcher itself never
        // 404s on probe today (it routes to ProcessContainerRuntime which
        // handles missing containers internally), but we accept it for
        // forward compatibility with future runtimes that surface it.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, ct);
            throw new InvalidOperationException(
                $"Dispatcher returned {(int)response.StatusCode} probing {containerId} at {url}: {body}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<DispatcherProbeResponse>(JsonOptions, ct);
        return parsed?.Healthy ?? false;
    }

    /// <inheritdoc />
    public async Task<ContainerResult> WaitForExitAsync(string containerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var httpClient = CreateClient();
        var uri = $"v1/containers/{Uri.EscapeDataString(containerId)}/wait-for-exit";

        // Long-poll: the dispatcher holds the connection open until the
        // container actually exits. The HttpClient's per-request timeout is
        // governed by the worker-side configuration (DispatcherClientOptions
        // sets a long default). Caller-provided cancellation is honoured.
        using var response = await httpClient.PostAsync(uri, content: null, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Container '{containerId}' is not known to the dispatcher.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, ct);
            throw new InvalidOperationException(
                $"Dispatcher returned {(int)response.StatusCode} waiting on container {containerId}: {body}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<DispatcherWaitForExitResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException(
                "Dispatcher returned an empty response body for the wait-for-exit call.");

        return new ContainerResult(
            ContainerId: parsed.Id,
            ExitCode: parsed.ExitCode,
            StandardOutput: parsed.StandardOutput ?? string.Empty,
            StandardError: parsed.StandardError ?? string.Empty);
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

        var httpClient = CreateClient();
        var uri = $"v1/containers/{Uri.EscapeDataString(containerId)}/a2a";
        var request = new DispatcherSendA2ARequest
        {
            Url = url,
            BodyBase64 = body.Length == 0 ? string.Empty : Convert.ToBase64String(body),
        };

        using var response = await httpClient.PostAsJsonAsync(uri, request, JsonOptions, ct);

        // Mirror ProbeContainerHttpAsync: 404 (container unknown) collapses
        // to a 502 so the worker's retry/timeout policy owns the next move
        // uniformly. Note that this is the "we couldn't reach the agent at
        // all" surface — the A2A SDK never sees a 404 because of it; it
        // sees a 502 from the proxy and decides whether to retry.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new ContainerHttpResponse(StatusCode: 502, Body: []);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body2 = await SafeReadBodyAsync(response, ct);
            throw new InvalidOperationException(
                $"Dispatcher returned {(int)response.StatusCode} forwarding A2A POST to {containerId} ({url}): {body2}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<DispatcherSendA2AResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException(
                "Dispatcher returned an empty response body for the A2A proxy call.");

        var bytes = string.IsNullOrEmpty(parsed.BodyBase64)
            ? []
            : Convert.FromBase64String(parsed.BodyBase64);
        return new ContainerHttpResponse(parsed.StatusCode, bytes);
    }

    /// <inheritdoc />
    public async Task EnsureVolumeAsync(string volumeName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        var httpClient = CreateClient();
        var request = new DispatcherCreateVolumeRequest { Name = volumeName };

        _logger.LogInformation(
            "Requesting dispatcher to ensure volume {VolumeName}", volumeName);

        using var response = await httpClient.PostAsJsonAsync(
            "v1/volumes", request, JsonOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, ct);
            throw new InvalidOperationException(
                $"Dispatcher returned {(int)response.StatusCode} ensuring volume {volumeName}: {body}");
        }
    }

    /// <inheritdoc />
    public async Task RemoveVolumeAsync(string volumeName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        var httpClient = CreateClient();
        var uri = $"v1/volumes/{Uri.EscapeDataString(volumeName)}";

        _logger.LogInformation(
            "Requesting dispatcher to remove volume {VolumeName}", volumeName);

        using var response = await httpClient.DeleteAsync(uri, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var body = await SafeReadBodyAsync(response, ct);
            throw new InvalidOperationException(
                $"Dispatcher returned {(int)response.StatusCode} removing volume {volumeName}: {body}");
        }
    }

    /// <inheritdoc />
    public async Task<VolumeMetrics?> GetVolumeMetricsAsync(string volumeName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        var httpClient = CreateClient();
        var uri = $"v1/volumes/{Uri.EscapeDataString(volumeName)}/metrics";

        using var response = await httpClient.GetAsync(uri, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            // Metrics are best-effort: return null on any error rather than
            // surfacing a hard failure to the metrics sweep.
            _logger.LogDebug(
                "Dispatcher returned {StatusCode} querying volume metrics for {VolumeName}; skipping",
                (int)response.StatusCode, volumeName);
            return null;
        }

        var parsed = await response.Content.ReadFromJsonAsync<DispatcherVolumeMetricsResponse>(JsonOptions, ct);
        return parsed is null
            ? null
            : new VolumeMetrics(parsed.SizeBytes, parsed.LastWrite);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListVolumesAsync(string prefix, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var httpClient = CreateClient();
        var uri = $"v1/volumes?prefix={Uri.EscapeDataString(prefix)}";

        try
        {
            using var response = await httpClient.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Best-effort: the GC reconciler must not crash on a transient
                // dispatcher hiccup — return empty so this sweep is a no-op.
                _logger.LogWarning(
                    "Dispatcher returned {StatusCode} listing volumes with prefix {Prefix}; returning empty",
                    (int)response.StatusCode, prefix);
                return Array.Empty<string>();
            }

            var parsed = await response.Content.ReadFromJsonAsync<DispatcherListVolumesResponse>(JsonOptions, ct);
            return parsed?.Names ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "Failed to list volumes with prefix {Prefix} via the dispatcher; returning empty", prefix);
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc />
    public async Task CreateNetworkAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var httpClient = CreateClient();
        var request = new DispatcherCreateNetworkRequest { Name = name };

        _logger.LogInformation(
            "Requesting dispatcher to create network {NetworkName}", name);

        using var response = await httpClient.PostAsJsonAsync(
            "v1/networks", request, JsonOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, ct);
            throw new InvalidOperationException(
                $"Dispatcher returned {(int)response.StatusCode} creating network {name}: {body}");
        }
    }

    /// <inheritdoc />
    public async Task RemoveNetworkAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var httpClient = CreateClient();
        var uri = $"v1/networks/{Uri.EscapeDataString(name)}";

        _logger.LogInformation(
            "Requesting dispatcher to remove network {NetworkName}", name);

        using var response = await httpClient.DeleteAsync(uri, ct);
        // The dispatcher already treats "missing on remove" as success (204);
        // we only treat anything else non-2xx as a failure here. NotFound is
        // accepted defensively in case a future dispatcher version surfaces
        // it instead — the contract is "remove is idempotent".
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var body = await SafeReadBodyAsync(response, ct);
            throw new InvalidOperationException(
                $"Dispatcher returned {(int)response.StatusCode} removing network {name}: {body}");
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(string containerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var httpClient = CreateClient();
        var uri = $"v1/containers/{Uri.EscapeDataString(containerId)}";

        _logger.LogInformation(
            "Requesting dispatcher stop of container {ContainerId}", containerId);

        using var response = await httpClient.DeleteAsync(uri, ct);
        // 404 is accepted as "already gone" to keep parity with the in-process
        // runtime which treats stop of an already-removed container as a no-op.
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var body = await SafeReadBodyAsync(response, ct);
            throw new InvalidOperationException(
                $"Dispatcher returned {(int)response.StatusCode} stopping container {containerId}: {body}");
        }
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        if (client.BaseAddress is null)
        {
            // BaseUrl shape is validated at startup by
            // DispatcherConfigurationRequirement (#639). We keep a defensive
            // throw here so hosts that bypass the validator don't silently
            // fall through to HttpClient with a null BaseAddress.
            if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            {
                throw new InvalidOperationException(
                    "Dispatcher:BaseUrl is not configured. Set it to the spring-dispatcher HTTP endpoint "
                    + "(e.g. http://host.containers.internal:8090/ — the dispatcher runs on the host, not in a container; see issue #1063). "
                    + "Startup configuration validation should have surfaced this before first call — see the /system/configuration report.");
            }
            client.BaseAddress = new Uri(_options.BaseUrl.EndsWith('/') ? _options.BaseUrl : _options.BaseUrl + "/");
        }

        if (!string.IsNullOrWhiteSpace(_options.BearerToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.BearerToken);
        }

        return client;
    }

    private async Task<DispatcherRunResponse> SendRunAsync(DispatcherRunRequest request, CancellationToken ct)
    {
        var httpClient = CreateClient();

        _logger.LogInformation(
            "Requesting dispatcher {Op} for image {Image}",
            request.Detached ? "start" : "run", request.Image);

        using var response = await httpClient.PostAsJsonAsync("v1/containers", request, JsonOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, ct);
            throw new InvalidOperationException(
                $"Dispatcher returned {(int)response.StatusCode} for container request: {body}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<DispatcherRunResponse>(JsonOptions, ct);
        return parsed ?? throw new InvalidOperationException(
            "Dispatcher returned an empty response body.");
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static DispatcherRunRequest BuildRunRequest(ContainerConfig config, bool detached)
    {
        return new DispatcherRunRequest
        {
            Image = config.Image,
            // Send argv as a list (CommandArgs). The legacy single-string
            // `Command` field is kept on the wire for older dispatchers but
            // is no longer populated by this client — the server prefers
            // CommandArgs when present. See #1093.
            CommandArgs = config.Command,
            Env = config.EnvironmentVariables is null
                ? null
                : new Dictionary<string, string>(config.EnvironmentVariables),
            Mounts = config.VolumeMounts,
            WorkingDirectory = config.WorkingDirectory,
            TimeoutSeconds = config.Timeout is { } t ? (int)t.TotalSeconds : null,
            NetworkName = config.NetworkName,
            AdditionalNetworks = config.AdditionalNetworks,
            Labels = config.Labels is null
                ? null
                : new Dictionary<string, string>(config.Labels),
            ExtraHosts = config.ExtraHosts,
            ContainerName = config.ContainerName,
            Entrypoint = config.Entrypoint,
            Detached = detached,
        };
    }

    /// <summary>
    /// Wire shape sent to <c>POST /v1/containers</c>. Duplicated here rather
    /// than taking a dependency on <c>Cvoya.Spring.Dispatcher</c> so the
    /// client and server can be deployed as independent binaries with no
    /// shared code package beyond <c>Cvoya.Spring.Core</c>.
    /// </summary>
    internal record DispatcherRunRequest
    {
        public required string Image { get; init; }

        /// <summary>
        /// Legacy single-string command field. Retained on the wire so a
        /// new client can still talk to an old dispatcher that does not
        /// know <see cref="CommandArgs"/>. The current client never
        /// populates this — it sends argv via <see cref="CommandArgs"/>
        /// and leaves this null.
        /// </summary>
        [JsonPropertyName("command")]
        public string? Command { get; init; }

        /// <summary>
        /// argv-style command vector. Each entry becomes one argv token
        /// inside the container — no shell splitting on either side. The
        /// dispatcher prefers this over <see cref="Command"/> when both
        /// are sent. Introduced in #1093 to replace the whitespace-split
        /// fragility of <c>ProcessContainerRuntime</c> (cf. #1063).
        /// </summary>
        [JsonPropertyName("commandArgs")]
        public IReadOnlyList<string>? CommandArgs { get; init; }

        public IDictionary<string, string>? Env { get; init; }
        public IReadOnlyList<string>? Mounts { get; init; }

        [JsonPropertyName("workdir")]
        public string? WorkingDirectory { get; init; }

        public int? TimeoutSeconds { get; init; }

        [JsonPropertyName("network")]
        public string? NetworkName { get; init; }

        /// <summary>
        /// Additional networks the dispatcher should attach the container to
        /// alongside <see cref="NetworkName"/>. Mirrors
        /// <c>RunContainerRequest.AdditionalNetworks</c> on the dispatcher side;
        /// duplicated here so the worker package does not take a build dependency
        /// on the dispatcher package. See ADR 0028 / issue #1166.
        /// </summary>
        [JsonPropertyName("additionalNetworks")]
        public IReadOnlyList<string>? AdditionalNetworks { get; init; }

        public IDictionary<string, string>? Labels { get; init; }
        public IReadOnlyList<string>? ExtraHosts { get; init; }
        public string? ContainerName { get; init; }
        public bool Detached { get; init; }

        /// <summary>
        /// Override for the image's <c>ENTRYPOINT</c> (#1686). When non-null
        /// the dispatcher emits <c>--entrypoint</c> to the container runtime.
        /// Mirrors <c>RunContainerRequest.Entrypoint</c>; null means inherit
        /// the image's declared entrypoint.
        /// </summary>
        [JsonPropertyName("entrypoint")]
        public string? Entrypoint { get; init; }
    }

    /// <summary>
    /// Wire shape sent to <c>POST /v1/images/pull</c>. Kept private to this
    /// client — the dispatcher side owns the server definition.
    /// </summary>
    internal record DispatcherPullRequest
    {
        public required string Image { get; init; }
        public int? TimeoutSeconds { get; init; }
    }

    /// <summary>
    /// Wire shape sent to <c>POST /v1/networks</c>. Mirrors
    /// <c>CreateNetworkRequest</c> on the dispatcher side; duplicated here
    /// rather than shared so client and server can be deployed independently.
    /// </summary>
    internal record DispatcherCreateNetworkRequest
    {
        public required string Name { get; init; }
    }

    /// <summary>
    /// Wire shape sent to <c>POST /v1/containers/{id}/probe</c>.
    /// </summary>
    internal record DispatcherProbeRequest
    {
        public required string Url { get; init; }
    }

    /// <summary>
    /// Wire shape returned by <c>POST /v1/containers/{id}/probe</c>.
    /// </summary>
    internal record DispatcherProbeResponse
    {
        public required bool Healthy { get; init; }
    }

    /// <summary>
    /// Wire shape returned by <c>POST /v1/containers/{id}/wait-for-exit</c> —
    /// the long-poll primitive added in #2198 so <c>ContainerLifecycleManager</c>
    /// can decompose Run into Start + (probe daprd via exec into app) + Wait.
    /// </summary>
    internal record DispatcherWaitForExitResponse
    {
        public required string Id { get; init; }
        public required int ExitCode { get; init; }

        [JsonPropertyName("stdout")]
        public string? StandardOutput { get; init; }

        [JsonPropertyName("stderr")]
        public string? StandardError { get; init; }
    }

    /// <summary>
    /// Wire shape sent to <c>POST /v1/containers/{id}/a2a</c> — the
    /// dispatcher-proxied A2A message-send primitive (#1160). Mirrors
    /// <c>SendContainerHttpJsonRequest</c> on the dispatcher side; duplicated
    /// here so the worker package does not take a build dependency on the
    /// dispatcher package.
    /// </summary>
    internal record DispatcherSendA2ARequest
    {
        public required string Url { get; init; }
        public required string BodyBase64 { get; init; }
    }

    /// <summary>
    /// Wire shape returned by <c>POST /v1/containers/{id}/a2a</c>.
    /// </summary>
    internal record DispatcherSendA2AResponse
    {
        public required int StatusCode { get; init; }
        public required string BodyBase64 { get; init; }
    }

    /// <summary>
    /// Wire shape returned by <c>GET /v1/containers/{id}/health</c>.
    /// Maps to <see cref="ContainerHealth"/> after deserialization.
    /// </summary>
    internal record DispatcherContainerHealthResponse
    {
        /// <summary><c>"healthy"</c> or <c>"unhealthy"</c>.</summary>
        public required string Status { get; init; }

        /// <summary>Reason phrase when unhealthy; absent on success.</summary>
        public string? Reason { get; init; }

        /// <summary>Probe method description when healthy; absent on failure.</summary>
        public string? Method { get; init; }
    }

    /// <summary>
    /// Wire shape returned by <c>POST /v1/containers</c>.
    /// </summary>
    internal record DispatcherRunResponse
    {
        public required string Id { get; init; }
        public int? ExitCode { get; init; }

        [JsonPropertyName("stdout")]
        public string? StandardOutput { get; init; }

        [JsonPropertyName("stderr")]
        public string? StandardError { get; init; }
    }

    /// <summary>
    /// Wire shape sent to <c>POST /v1/volumes</c> — request the dispatcher to
    /// create a named volume idempotently. Duplicated from the dispatcher's own
    /// model so the worker package stays free of dispatcher build dependencies.
    /// </summary>
    internal record DispatcherCreateVolumeRequest
    {
        public required string Name { get; init; }
    }

    /// <summary>
    /// Wire shape returned by <c>GET /v1/volumes/{name}/metrics</c>.
    /// Fields mirror <see cref="VolumeMetrics"/> so the client can construct one
    /// without deserialising the full inspect blob.
    /// </summary>
    internal record DispatcherVolumeMetricsResponse
    {
        public long? SizeBytes { get; init; }
        public DateTimeOffset? LastWrite { get; init; }
    }

    /// <summary>
    /// Wire shape returned by <c>GET /v1/volumes?prefix=</c> — the names of all
    /// volumes whose name begins with the requested prefix (#3005).
    /// </summary>
    internal record DispatcherListVolumesResponse
    {
        public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();
    }
}
