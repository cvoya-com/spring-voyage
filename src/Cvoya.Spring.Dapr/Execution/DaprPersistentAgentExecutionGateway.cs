// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core;

using global::Dapr.Client;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IPersistentAgentExecutionGateway"/> implementation that
/// delegates every persistent-agent lifecycle operation to the execution
/// host (<c>spring-worker</c>) over Dapr service invocation (ADR-0052 /
/// Wave 3 / #2618).
/// </summary>
/// <remarks>
/// <para>
/// Dapr service invocation is the transport: <c>spring-api</c> and
/// <c>spring-worker</c> are both Dapr apps with sidecars, so an
/// api→worker call rides the mesh — no extra network wiring, no extra
/// bearer token, mTLS and retry handled by the sidecars. The invocation goes
/// through an <see cref="HttpClient"/> obtained from
/// <see cref="DaprClient.CreateInvokableHttpClient(string)"/>, whose requests
/// route through the local Dapr sidecar to the target app. The worker's
/// internal execution routes (<c>internal/agents/{id}/...</c>) are reachable
/// only through the Dapr app channel, not the public ingress.
/// </para>
/// <para>
/// The worker returns <c>200</c> with the contract DTO on success and a
/// problem body (<c>{ detail }</c>) with <c>400</c> / <c>404</c> on failure;
/// this gateway translates a non-2xx into a <see cref="SpringException"/>
/// carrying the worker's detail so the API endpoint surfaces the same wire
/// error it produced when the work ran in-process.
/// </para>
/// </remarks>
public class DaprPersistentAgentExecutionGateway : IPersistentAgentExecutionGateway
{
    /// <summary>The Dapr app id of the execution host.</summary>
    public const string WorkerAppId = "spring-worker";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly ILogger<DaprPersistentAgentExecutionGateway> _logger;

    /// <summary>
    /// Initializes the gateway with a Dapr-invokable <see cref="HttpClient"/>
    /// bound to the execution host's app id.
    /// </summary>
    public DaprPersistentAgentExecutionGateway(
        DaprClient daprClient,
        ILogger<DaprPersistentAgentExecutionGateway> logger)
    {
        _client = daprClient.CreateInvokableHttpClient(WorkerAppId);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<PersistentAgentDeploymentState> DeployAsync(
        string agentActorId, string? imageOverride, CancellationToken cancellationToken)
        => InvokeForStateAsync(
            HttpMethod.Post,
            $"internal/agents/{agentActorId}/deploy",
            new PersistentAgentDeployRequest(imageOverride),
            cancellationToken);

    /// <inheritdoc />
    public Task<PersistentAgentDeploymentState> UndeployAsync(
        string agentActorId, CancellationToken cancellationToken)
        => InvokeForStateAsync(
            HttpMethod.Post,
            $"internal/agents/{agentActorId}/undeploy",
            body: null,
            cancellationToken);

    /// <inheritdoc />
    public Task<PersistentAgentDeploymentState> ScaleAsync(
        string agentActorId, int replicas, CancellationToken cancellationToken)
        => InvokeForStateAsync(
            HttpMethod.Post,
            $"internal/agents/{agentActorId}/scale",
            new PersistentAgentScaleRequest(replicas),
            cancellationToken);

    /// <inheritdoc />
    public Task<PersistentAgentDeploymentState> GetDeploymentAsync(
        string agentActorId, CancellationToken cancellationToken)
        => InvokeForStateAsync(
            HttpMethod.Get,
            $"internal/agents/{agentActorId}/deployment",
            body: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<PersistentAgentLogsState> GetLogsAsync(
        string agentActorId, int tail, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"internal/agents/{agentActorId}/logs?tail={tail}",
            body: null,
            cancellationToken);

        await ThrowIfNotSuccessAsync(response, agentActorId, cancellationToken);

        return await response.Content.ReadFromJsonAsync<PersistentAgentLogsState>(
                JsonOptions, cancellationToken)
            ?? throw new SpringException(
                $"Execution host returned an empty logs body for agent '{agentActorId}'.");
    }

    private async Task<PersistentAgentDeploymentState> InvokeForStateAsync(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, body, cancellationToken);
        await ThrowIfNotSuccessAsync(response, path, cancellationToken);

        return await response.Content.ReadFromJsonAsync<PersistentAgentDeploymentState>(
                JsonOptions, cancellationToken)
            ?? throw new SpringException(
                $"Execution host returned an empty deployment body for '{path}'.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        }

        try
        {
            return await _client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Dapr service invocation to {App}/{Path} failed.", WorkerAppId, path);
            throw new SpringException(
                $"Failed to reach the execution host ({WorkerAppId}) for '{path}': {ex.Message}",
                ex);
        }
    }

    private static async Task ThrowIfNotSuccessAsync(
        HttpResponseMessage response, string context, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await ReadProblemDetailAsync(response, cancellationToken);

        // The worker emits 400 for rejected operations (no execution config,
        // not persistent, readiness failure, scale > 1) and 404 for
        // "agent not deployed". The API endpoint handlers translate a
        // SpringException into the matching wire status; carrying the
        // worker's detail keeps the operator-facing message identical to the
        // pre-delegation in-process path.
        throw new SpringException(
            detail ?? $"Execution host returned {(int)response.StatusCode} for '{context}'.");
    }

    private static async Task<string?> ReadProblemDetailAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength is 0)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String)
            {
                return detail.GetString();
            }

            return null;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
            return null;
        }
    }
}
