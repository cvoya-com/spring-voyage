// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

/// <summary>
/// Wire contracts for the internal persistent-agent execution surface
/// (ADR-0052 / Wave 3 / #2618). The execution host (<c>spring-worker</c>)
/// owns the persistent-agent containers; the HTTP front door
/// (<c>spring-api</c>) delegates every deploy / undeploy / scale /
/// deployment-status / logs operation to the worker over Dapr service
/// invocation rather than resolving the execution singletons in-process.
/// </summary>
/// <remarks>
/// These records are an <em>internal</em> contract between the two .NET
/// hosts — they do not appear in the public OpenAPI document. The API host's
/// public endpoints (<c>POST /api/v1/tenant/agents/{id}/deploy</c> etc.) keep
/// their existing request / response shapes; only their implementation
/// changes from in-process to delegated.
/// </remarks>

/// <summary>
/// Request body for the worker's deploy endpoint.
/// </summary>
/// <param name="ImageOverride">
/// Optional one-shot image override; not persisted onto the definition.
/// </param>
public sealed record PersistentAgentDeployRequest(string? ImageOverride = null);

/// <summary>
/// Request body for the worker's scale endpoint.
/// </summary>
/// <param name="Replicas">Desired replica count (OSS core supports 0 or 1).</param>
public sealed record PersistentAgentScaleRequest(int Replicas);

/// <summary>
/// Deployment state of a persistent agent, as observed by the execution
/// host's <see cref="PersistentAgentRegistry"/>. Mirrors the fields the API
/// host's <c>PersistentAgentDeploymentResponse</c> exposes — the API host
/// projects this internal shape onto that public response.
/// </summary>
/// <param name="AgentId">The agent's actor id (32-char no-dash hex).</param>
/// <param name="Running"><c>true</c> when a container is tracked and reachable.</param>
/// <param name="HealthStatus"><c>healthy</c> / <c>unhealthy</c> / <c>unknown</c>.</param>
/// <param name="Image">The image the deployment was started with, when known.</param>
/// <param name="Endpoint">The A2A endpoint the dispatcher dials, when tracked.</param>
/// <param name="ContainerId">The backing container id, when tracked.</param>
/// <param name="StartedAt">When the deployment was registered, when tracked.</param>
/// <param name="ConsecutiveFailures">Rolling health-monitor failure count.</param>
public sealed record PersistentAgentDeploymentState(
    string AgentId,
    bool Running,
    string HealthStatus,
    string? Image,
    string? Endpoint,
    string? ContainerId,
    DateTimeOffset? StartedAt,
    int ConsecutiveFailures)
{
    /// <summary>
    /// The canonical "not running" state for an agent that has no tracked
    /// deployment (never deployed, or already undeployed).
    /// </summary>
    public static PersistentAgentDeploymentState NotRunning(string agentId) =>
        new(agentId, Running: false, HealthStatus: "unknown", Image: null,
            Endpoint: null, ContainerId: null, StartedAt: null, ConsecutiveFailures: 0);
}

/// <summary>
/// Response body for the worker's logs endpoint.
/// </summary>
/// <param name="AgentId">The agent's actor id.</param>
/// <param name="ContainerId">The container the logs were read from.</param>
/// <param name="Tail">The tail window the worker used.</param>
/// <param name="Logs">The captured log tail.</param>
public sealed record PersistentAgentLogsState(
    string AgentId,
    string ContainerId,
    int Tail,
    string Logs);
