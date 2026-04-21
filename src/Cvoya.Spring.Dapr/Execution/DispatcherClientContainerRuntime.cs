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

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response, timeoutCts.Token);
                throw new InvalidOperationException(
                    $"Dispatcher returned {(int)response.StatusCode} pulling image {image}: {body}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
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
                    "Dispatcher:BaseUrl is not configured. Set it to the spring-dispatcher HTTP endpoint (e.g. http://spring-dispatcher:8080/). "
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