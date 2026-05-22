// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core;

/// <summary>
/// The HTTP front door's view of the execution host's persistent-agent
/// lifecycle surface (ADR-0052 / Wave 3 / #2618). The API host's
/// <c>AgentEndpoints</c> / <c>UnitEndpoints</c> resolve this gateway instead
/// of injecting the execution singletons (<c>PersistentAgentLifecycle</c>,
/// <c>PersistentAgentRegistry</c>, …) — those singletons are
/// execution-host-only and never register under
/// <see cref="DependencyInjection.SpringHostRole.HttpFrontDoor"/>.
/// </summary>
/// <remarks>
/// The default implementation, <see cref="DaprPersistentAgentExecutionGateway"/>,
/// delegates to the worker over Dapr service invocation. Deploy / undeploy /
/// scale / deployment-status / logs are all delegated uniformly — there is no
/// partial in-process read path on the API host.
/// </remarks>
public interface IPersistentAgentExecutionGateway
{
    /// <summary>
    /// Deploys (or reconciles) a persistent agent's backing container.
    /// </summary>
    /// <exception cref="SpringException">
    /// Thrown when the worker rejects the deploy (no execution config, not
    /// persistent, readiness failure, …).
    /// </exception>
    Task<PersistentAgentDeploymentState> DeployAsync(
        string agentActorId, string? imageOverride, CancellationToken cancellationToken);

    /// <summary>Tears down a persistent agent's backing container. Idempotent.</summary>
    Task<PersistentAgentDeploymentState> UndeployAsync(
        string agentActorId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a replica-count change (OSS core supports 0 or 1).
    /// </summary>
    /// <exception cref="SpringException">
    /// Thrown when the worker rejects the scale request.
    /// </exception>
    Task<PersistentAgentDeploymentState> ScaleAsync(
        string agentActorId, int replicas, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the current deployment state of a persistent agent. Returns the
    /// canonical "not running" state when no deployment is tracked.
    /// </summary>
    Task<PersistentAgentDeploymentState> GetDeploymentAsync(
        string agentActorId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the tail of a persistent agent's container logs.
    /// </summary>
    /// <exception cref="SpringException">
    /// Thrown when the agent is not currently deployed.
    /// </exception>
    Task<PersistentAgentLogsState> GetLogsAsync(
        string agentActorId, int tail, CancellationToken cancellationToken);
}
