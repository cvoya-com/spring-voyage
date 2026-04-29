// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Execution;

using global::Dapr.Actors;

/// <summary>
/// Dapr virtual actor that supervises the lifecycle of one tenant agent container
/// (D3d — ADR-0029 § "Failure recovery").
///
/// One supervisor actor exists per agent (keyed by agent id). It owns:
/// <list type="bullet">
///   <item>Starting the container via <c>ContainerLifecycleManager</c>, with the
///         workspace volume provisioned by <c>AgentVolumeManager</c> (D3c).</item>
///   <item>Crash detection — polling the container's health signal and treating a
///         non-running container as a crash.</item>
///   <item>Restart — re-launching the container against the same persistent workspace
///         volume so <c>initialize()</c> sees existing checkpoint state (agent-owned
///         recovery per ADR-0029 § 3.3).</item>
///   <item>Reclaim-on-done — calling <c>AgentVolumeManager.ReclaimAsync</c> when
///         an ephemeral agent declares work done; persistent agents keep their
///         volume until <see cref="StopAsync"/> is called.</item>
/// </list>
///
/// <para>
/// Container lifecycle is platform-internal and out-of-band of A2A (per ADR-0029
/// § "Wire protocol"). The supervisor uses <c>IContainerRuntime</c> APIs only;
/// it never sends A2A messages to manage the container.
/// </para>
/// <para>
/// The actor does not dual-home onto the tenant network. It lives on
/// <c>spring-net</c> alongside the other platform actors and reaches the
/// container-runtime API through the dispatcher's <c>IContainerRuntime</c>
/// abstraction.
/// </para>
/// </summary>
public interface IContainerSupervisorActor : IActor
{
    /// <summary>
    /// Provisions the workspace volume, starts the agent container, and
    /// registers the container in the supervisor's state so crash-detection
    /// and restart can kick in. Idempotent — if the container is already
    /// running and healthy, returns the current entry without touching it.
    /// </summary>
    /// <param name="request">The launch parameters for the agent container.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The container id of the running agent container.
    /// </returns>
    Task<string> StartAsync(SupervisorLaunchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signals that the supervised agent has finished its work (ephemeral
    /// completion path). Stops the container, then reclaims the workspace
    /// volume per ADR-0029 § 3.2 (ephemeral agents: volume reclaimed on
    /// completion, not on crash). Idempotent.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task DoneAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops and removes the supervised container without reclaiming the
    /// workspace volume. Used for persistent agents whose volume must
    /// survive an explicit undeploy so a later redeploy resumes from
    /// existing state. For full teardown (including volume removal) call
    /// <see cref="DoneAsync"/> instead.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current supervision state for this agent container.
    /// </summary>
    Task<SupervisorState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers one crash-detection and restart cycle. Normally driven by
    /// the actor's own reminder; exposed on the interface so operators and
    /// tests can request a poll without waiting for the next scheduled tick.
    /// </summary>
    Task CheckHealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Parameters required to launch a supervised agent container.
/// </summary>
/// <param name="AgentId">The stable agent identifier used to key the workspace volume and the supervisor actor.</param>
/// <param name="Image">The container image to launch.</param>
/// <param name="EnvironmentVariables">
/// Environment variables to inject into the container. The supervisor
/// merges in the mandatory <c>SPRING_WORKSPACE_PATH</c> env var
/// automatically; callers should populate the rest of the
/// <c>IAgentContext</c> fields (§ 2.2 of the D1 spec).
/// </param>
/// <param name="Hosting">
/// <see cref="AgentHostingMode.Ephemeral"/> or
/// <see cref="AgentHostingMode.Persistent"/>. Controls volume reclamation
/// semantics: ephemeral agents reclaim on <see cref="IContainerSupervisorActor.DoneAsync"/>;
/// persistent agents keep the volume until explicit deletion.
/// </param>
/// <param name="NetworkName">Optional network to attach the container to.</param>
/// <param name="AdditionalNetworks">Optional additional networks for the container.</param>
/// <param name="MaxRestarts">
/// Maximum number of automatic restarts the supervisor will attempt before
/// giving up and marking the agent as permanently failed. Defaults to
/// <see cref="ContainerSupervisorActor.DefaultMaxRestarts"/>.
/// </param>
public record SupervisorLaunchRequest(
    string AgentId,
    string Image,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    AgentHostingMode Hosting = AgentHostingMode.Ephemeral,
    string? NetworkName = null,
    IReadOnlyList<string>? AdditionalNetworks = null,
    int? MaxRestarts = null);

/// <summary>
/// The supervisor's view of the managed agent container at a point in time.
/// </summary>
public enum ContainerSupervisionStatus
{
    /// <summary>No container has been started yet.</summary>
    Idle,

    /// <summary>The container is running and believed to be healthy.</summary>
    Running,

    /// <summary>
    /// A crash was detected; the supervisor is preparing to restart.
    /// </summary>
    CrashDetected,

    /// <summary>
    /// The container was stopped via <see cref="IContainerSupervisorActor.StopAsync"/>
    /// or <see cref="IContainerSupervisorActor.DoneAsync"/>; no further restarts.
    /// </summary>
    Stopped,

    /// <summary>
    /// The container has crashed more than the configured restart limit; the
    /// supervisor has given up.
    /// </summary>
    Failed,
}

/// <summary>
/// Persisted state for a <see cref="ContainerSupervisorActor"/>.
/// </summary>
/// <param name="Image">
/// The container image used on the last successful
/// <see cref="IContainerSupervisorActor.StartAsync"/> call. Persisted so the
/// supervisor can self-heal on crash without a new <c>StartAsync</c> call from the
/// dispatcher. Credentials and full environment variables are NOT persisted here
/// (they would expire); a restart that needs fresh credentials requires an
/// explicit <c>Stop + StartAsync</c> cycle.
/// </param>
public record SupervisorState(
    string AgentId,
    AgentHostingMode Hosting,
    ContainerSupervisionStatus Status,
    string? ContainerId,
    string? SidecarId,
    string? NetworkName,
    string? VolumeName,
    int RestartCount,
    int MaxRestarts,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastCrashAt,
    string? Image = null);