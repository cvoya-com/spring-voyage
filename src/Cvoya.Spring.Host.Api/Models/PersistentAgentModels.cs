// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Request body for <c>POST /api/v1/agents/{id}/deploy</c> — stands up the
/// persistent agent service backing <paramref name="AgentId"/>. The
/// endpoint is idempotent: deploying an agent that is already running and
/// healthy is a no-op and returns the current deployment state so callers
/// can treat the call as a reconcile.
/// </summary>
/// <param name="Image">
/// Optional image override. When <c>null</c>, the server uses the image
/// recorded in the agent's stored <c>execution.image</c>. A caller passing
/// an override does NOT persist it — the override is applied to this
/// deployment only so operators can smoke-test candidate images before
/// updating the agent definition.
/// </param>
/// <param name="Replicas">
/// Desired replica count. Defaults to 1. The OSS core supports single-
/// replica persistent agents today; passing <c>&gt;1</c> returns a 400 with
/// a clear "not supported yet" message so the CLI can tell the operator to
/// stay on 1 until horizontal scale lands (tracked as a follow-up).
/// </param>
public record DeployPersistentAgentRequest(
    string? Image = null,
    int? Replicas = null);

/// <summary>
/// Response body describing the current persistent-agent deployment state.
/// Shared by <c>POST /deploy</c>, <c>POST /scale</c>, and the extended
/// <c>GET</c> status endpoint.
/// </summary>
/// <param name="AgentId">The agent identifier.</param>
/// <param name="Running">
/// <c>true</c> when the persistent registry holds an entry for this agent
/// and the backing container is at least reachable (even if its latest
/// probe is failing). <c>false</c> when no deployment is tracked.
/// </param>
/// <param name="HealthStatus">
/// The registry's last health assessment — <c>"healthy"</c>,
/// <c>"unhealthy"</c>, or <c>"unknown"</c> when no deployment exists.
/// </param>
/// <param name="Replicas">
/// Currently deployed replicas. Always 1 in the OSS core today; reserved
/// for the horizontal-scale follow-up.
/// </param>
/// <param name="Image">
/// The image the deployment was started with — either the override from
/// the deploy request or the agent definition's <c>execution.image</c>.
/// </param>
/// <param name="Endpoint">
/// The A2A endpoint the dispatcher dials, or <c>null</c> when no deployment
/// is tracked.
/// </param>
/// <param name="ContainerId">
/// The container identifier owned by the registry, or <c>null</c> when no
/// deployment is tracked.
/// </param>
/// <param name="StartedAt">
/// When the deployment was registered, or <c>null</c> when no deployment is
/// tracked.
/// </param>
/// <param name="ConsecutiveFailures">
/// Rolling failure count from the health monitor, or 0 when no deployment
/// is tracked.
/// </param>
public record PersistentAgentDeploymentResponse(
    string AgentId,
    bool Running,
    string HealthStatus,
    int Replicas,
    string? Image,
    string? Endpoint,
    string? ContainerId,
    DateTimeOffset? StartedAt,
    int ConsecutiveFailures);

/// <summary>
/// Request body for <c>POST /api/v1/agents/{id}/scale</c>. Only
/// <c>Replicas == 1</c> is supported by the OSS core today; this shape
/// keeps the CLI/API contract ready for the horizontal-scale follow-up so
/// clients don't need to change wire shape when it lands.
/// </summary>
/// <param name="Replicas">The target replica count.</param>
public record ScalePersistentAgentRequest(int Replicas);

/// <summary>
/// Response body for <c>GET /api/v1/agents/{id}/logs</c>. Returns the tail
/// of the deployment's combined stdout+stderr. When the agent is not
/// deployed the endpoint returns a 404 (the CLI surfaces the error
/// message verbatim).
/// </summary>
/// <param name="AgentId">The agent identifier.</param>
/// <param name="ContainerId">
/// The container the logs were read from.
/// </param>
/// <param name="Tail">The tail window the server used.</param>
/// <param name="Logs">The captured log tail.</param>
public record PersistentAgentLogsResponse(
    string AgentId,
    string ContainerId,
    int Tail,
    string Logs);