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
/// as <c>A2AExecutionDispatcher</c> and <c>WorkflowOrchestrationStrategy</c>.
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
            Command = config.Command,
            Env = config.EnvironmentVariables is null
                ? null
                : new Dictionary<string, string>(config.EnvironmentVariables),
            Mounts = config.VolumeMounts,
            WorkingDirectory = config.WorkingDirectory,
            TimeoutSeconds = config.Timeout is { } t ? (int)t.TotalSeconds : null,
            NetworkName = config.NetworkName,
            Labels = config.Labels is null
                ? null
                : new Dictionary<string, string>(config.Labels),
            ExtraHosts = config.ExtraHosts,
            Detached = detached,
            Workspace = config.Workspace is { } ws
                ? new DispatcherWorkspace
                {
                    MountPath = ws.MountPath,
                    Files = ws.Files is IDictionary<string, string> mutable
                        ? mutable
                        : new Dictionary<string, string>(ws.Files),
                }
                : null,
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
        public string? Command { get; init; }
        public IDictionary<string, string>? Env { get; init; }
        public IReadOnlyList<string>? Mounts { get; init; }

        [JsonPropertyName("workdir")]
        public string? WorkingDirectory { get; init; }

        public int? TimeoutSeconds { get; init; }

        [JsonPropertyName("network")]
        public string? NetworkName { get; init; }

        public IDictionary<string, string>? Labels { get; init; }
        public IReadOnlyList<string>? ExtraHosts { get; init; }
        public bool Detached { get; init; }

        /// <summary>
        /// Per-invocation workspace the dispatcher must materialise on its own
        /// host filesystem and bind-mount into the container. <c>null</c>
        /// means the worker is asking for a plain run with no workspace.
        /// </summary>
        public DispatcherWorkspace? Workspace { get; init; }
    }

    /// <summary>
    /// Wire shape for the optional <c>workspace</c> field on
    /// <see cref="DispatcherRunRequest"/>. The dispatcher creates a fresh
    /// per-invocation directory, writes <see cref="Files"/> into it, and
    /// bind-mounts that directory at <see cref="MountPath"/> inside the
    /// container — see issue #1042.
    /// </summary>
    internal record DispatcherWorkspace
    {
        public required string MountPath { get; init; }
        public required IDictionary<string, string> Files { get; init; }
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
}