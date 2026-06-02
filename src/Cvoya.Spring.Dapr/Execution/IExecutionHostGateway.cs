// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core;

/// <summary>
/// The API host's delegation channel to the execution host — the HTTP front
/// door's view of the execution host's container-lifecycle surface (ADR-0052 /
/// Wave 3 / #2618, #2627). The API host's <c>AgentEndpoints</c> /
/// <c>UnitEndpoints</c> resolve this gateway instead of injecting the
/// execution singletons (<c>PersistentAgentLifecycle</c>,
/// <c>PersistentAgentRegistry</c>, <c>IUnitContainerLifecycle</c>, …) — those
/// singletons are execution-host-only and never register under
/// <see cref="DependencyInjection.SpringHostRole.HttpFrontDoor"/>, so the API
/// host registers zero execution services.
/// </summary>
/// <remarks>
/// The default implementation, <see cref="DaprExecutionHostGateway"/>,
/// delegates to the execution host (<c>spring-worker</c>) over Dapr service
/// invocation. Persistent-agent deploy / undeploy / scale / deployment-status /
/// logs and unit-container teardown are all delegated uniformly — there is no
/// partial in-process execution path on the API host.
/// </remarks>
public interface IExecutionHostGateway
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

    /// <summary>
    /// Tears down a persistent agent's backing container <b>and reclaims its
    /// per-agent workspace volume</b>. Reserved for genuine agent
    /// decommission (agent delete / unit force-delete); the reclaim wipes the
    /// agent's durable workspace per ADR-0029 ("reclaimed when the agent is
    /// deleted"). For a resumable stop that must keep the volume, use
    /// <see cref="StopAgentContainerAsync"/> instead (#2999). Idempotent.
    /// </summary>
    Task<PersistentAgentDeploymentState> UndeployAsync(
        string agentActorId, CancellationToken cancellationToken);

    /// <summary>
    /// Stops a persistent agent's backing container <b>without</b> reclaiming
    /// its per-agent workspace volume — the volume-preserving teardown for
    /// resumable stops (unit stop, agent undeploy, scale-to-zero), per
    /// ADR-0029 (#2999). The agent's durable memory + <c>claude --resume</c>
    /// transcripts survive so a later redeploy resumes. Idempotent — a no-op
    /// when nothing is deployed.
    /// </summary>
    Task<PersistentAgentDeploymentState> StopAgentContainerAsync(
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

    /// <summary>
    /// Tears down a unit's backing runtime container, Dapr sidecar, and
    /// network (#2627). Idempotent — a unit with no tracked container handle
    /// still completes successfully. Drives the force-delete unit-teardown
    /// path that previously resolved <c>IUnitContainerLifecycle</c> in-process
    /// on the API host.
    /// </summary>
    /// <exception cref="SpringException">
    /// Thrown when the worker cannot be reached or rejects the teardown.
    /// </exception>
    Task StopUnitContainerAsync(
        string unitActorId, CancellationToken cancellationToken);
}
