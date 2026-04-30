// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

/// <summary>
/// Dapr virtual actor that supervises the lifecycle of one tenant agent container
/// (D3d — ADR-0029 § "Failure recovery").
///
/// One actor instance exists per agent, keyed by agent id. It manages start,
/// crash detection, restart, and reclaim-on-done, delegating container operations
/// to <see cref="ContainerLifecycleManager"/> and volume operations to
/// <see cref="AgentVolumeManager"/>.
///
/// <para>
/// <b>Design choice: per-container supervisor.</b> Each agent gets its own
/// supervisor actor. This aligns with ADR-0026's one-container-per-agent scope,
/// keeps actor state small (one container's worth of info), and lets the Dapr
/// placement service distribute load linearly with container count. The
/// alternative (one supervisor with per-container state) would require more
/// complex state management and fanout within a single actor turn.
/// </para>
///
/// <para>
/// <b>No A2A involvement.</b> Container lifecycle is platform-internal and
/// out-of-band of A2A (ADR-0029 § "Wire protocol"). The supervisor uses
/// <see cref="IContainerRuntime"/> APIs only.
/// </para>
///
/// <para>
/// <b>No dual-homing.</b> The supervisor lives on <c>spring-net</c> alongside
/// the other platform actors. It reaches the container-runtime API through the
/// dispatcher's <see cref="IContainerRuntime"/> abstraction.
/// </para>
/// </summary>
public class ContainerSupervisorActor(
    ActorHost host,
    IContainerRuntime containerRuntime,
    ContainerLifecycleManager containerLifecycleManager,
    AgentVolumeManager volumeManager,
    ILoggerFactory loggerFactory)
    : Actor(host), IContainerSupervisorActor, IRemindable
{
    /// <summary>
    /// Name of the Dapr reminder that drives periodic crash-detection polls.
    /// </summary>
    internal const string HealthCheckReminderName = "supervisor-health-check";

    /// <summary>
    /// Default interval between health-check polls.
    /// </summary>
    internal static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default maximum number of automatic restarts before the supervisor gives up.
    /// </summary>
    public const int DefaultMaxRestarts = 5;

    /// <summary>
    /// State store key for the supervisor's persisted state.
    /// </summary>
    private const string SupervisorStateKey = "Supervisor:State";

    private readonly ILogger _logger = loggerFactory.CreateLogger<ContainerSupervisorActor>();

    /// <inheritdoc />
    public async Task<string> StartAsync(
        SupervisorLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await TryGetStateAsync(cancellationToken);

        // Idempotent: if we already have a running container, return it.
        if (existing is { Status: ContainerSupervisionStatus.Running, ContainerId: not null })
        {
            _logger.LogDebug(
                EventIds.SupervisorStartIdempotent,
                "Supervisor for agent {AgentId} already has a running container {ContainerId}; returning existing.",
                request.AgentId, existing.ContainerId);
            return existing.ContainerId;
        }

        _logger.LogInformation(
            EventIds.SupervisorStarting,
            "Supervisor for agent {AgentId} starting container (image={Image}, hosting={Hosting})",
            request.AgentId, request.Image, request.Hosting);

        // D3c: provision the workspace volume before starting the container.
        // Volume survives container restarts; ReclaimAsync is only called on
        // ephemeral completion (DoneAsync) or explicit persistent delete (StopAsync
        // does NOT reclaim — persistent agent keeps its workspace across undeploy).
        var volumeName = await volumeManager.EnsureAsync(request.AgentId, cancellationToken);
        var volumeMount = AgentVolumeManager.BuildVolumeMount(volumeName);

        // Build the environment variables, merging the workspace path env var
        // (mandatory per D1 spec § 2.2.1) into whatever the caller supplied.
        var env = BuildEnvironmentVariables(request.EnvironmentVariables);

        var config = new ContainerConfig(
            Image: request.Image,
            EnvironmentVariables: env,
            VolumeMounts: [volumeMount],
            NetworkName: request.NetworkName,
            AdditionalNetworks: request.AdditionalNetworks);

        var containerId = await containerRuntime.StartAsync(config, cancellationToken);

        var state = new SupervisorState(
            AgentId: request.AgentId,
            Hosting: request.Hosting,
            Status: ContainerSupervisionStatus.Running,
            ContainerId: containerId,
            SidecarId: null,
            NetworkName: request.NetworkName,
            VolumeName: volumeName,
            RestartCount: 0,
            MaxRestarts: request.MaxRestarts ?? DefaultMaxRestarts,
            LastStartedAt: DateTimeOffset.UtcNow,
            LastCrashAt: null,
            Image: request.Image);

        await SaveStateAsync(state, cancellationToken);

        // Register the health-check reminder so we poll for crashes.
        await RegisterReminderAsync(
            HealthCheckReminderName,
            null,
            HealthCheckInterval,
            HealthCheckInterval);

        _logger.LogInformation(
            EventIds.SupervisorStarted,
            "Supervisor for agent {AgentId} started container {ContainerId}",
            request.AgentId, containerId);

        return containerId;
    }

    /// <inheritdoc />
    public async Task DoneAsync(CancellationToken cancellationToken = default)
    {
        var state = await TryGetStateAsync(cancellationToken);
        if (state is null || state.Status == ContainerSupervisionStatus.Stopped)
        {
            return;
        }

        _logger.LogInformation(
            EventIds.SupervisorDone,
            "Supervisor for agent {AgentId} marking work done; stopping container {ContainerId}",
            state.AgentId, state.ContainerId);

        await StopContainerAsync(state, cancellationToken);

        // Ephemeral agents: reclaim the workspace volume when work is done.
        // Persistent agents: keep the volume (reclaim on explicit delete at a
        // higher level, not here — mirrors EphemeralAgentRegistry.ReleaseAsync).
        if (state.Hosting == AgentHostingMode.Ephemeral && state.VolumeName is not null)
        {
            await volumeManager.ReclaimAsync(state.AgentId, CancellationToken.None);
        }

        var stopped = state with
        {
            Status = ContainerSupervisionStatus.Stopped,
            ContainerId = null,
        };
        await SaveStateAsync(stopped, cancellationToken);

        await TryUnregisterReminderAsync();

        _logger.LogInformation(
            EventIds.SupervisorStopped,
            "Supervisor for agent {AgentId} stopped and done (hosting={Hosting})",
            state.AgentId, state.Hosting);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var state = await TryGetStateAsync(cancellationToken);
        if (state is null || state.Status == ContainerSupervisionStatus.Stopped)
        {
            return;
        }

        _logger.LogInformation(
            EventIds.SupervisorStopping,
            "Supervisor for agent {AgentId} stopping container {ContainerId} (no volume reclaim)",
            state.AgentId, state.ContainerId);

        await StopContainerAsync(state, cancellationToken);

        // Intentionally NOT reclaiming the volume — Stop is the persistent-agent
        // undeploy path; the volume survives so the next Deploy resumes from
        // existing workspace state.
        var stopped = state with
        {
            Status = ContainerSupervisionStatus.Stopped,
            ContainerId = null,
        };
        await SaveStateAsync(stopped, cancellationToken);

        await TryUnregisterReminderAsync();
    }

    /// <inheritdoc />
    public async Task<SupervisorState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var state = await TryGetStateAsync(cancellationToken);
        return state ?? new SupervisorState(
            AgentId: Id.GetId(),
            Hosting: AgentHostingMode.Ephemeral,
            Status: ContainerSupervisionStatus.Idle,
            ContainerId: null,
            SidecarId: null,
            NetworkName: null,
            VolumeName: null,
            RestartCount: 0,
            MaxRestarts: DefaultMaxRestarts,
            LastStartedAt: null,
            LastCrashAt: null);
    }

    /// <inheritdoc />
    public async Task CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var state = await TryGetStateAsync(cancellationToken);
        if (state is null
            || state.Status is ContainerSupervisionStatus.Stopped or ContainerSupervisionStatus.Failed
            || state.ContainerId is null)
        {
            return;
        }

        // Probe the container with a lightweight health check.
        // The canonical A2A health endpoint is /:8999/ but the actor does not
        // know the A2A port. We rely on the container runtime to tell us
        // whether the container process is still alive (non-running = crash).
        // Full A2A-level readiness is the dispatcher's concern; the supervisor
        // only cares about process liveness.
        var isAlive = await IsContainerAliveAsync(state.ContainerId, cancellationToken);

        if (isAlive)
        {
            _logger.LogDebug(
                EventIds.SupervisorHealthOk,
                "Health check: agent {AgentId} container {ContainerId} is alive",
                state.AgentId, state.ContainerId);
            return;
        }

        _logger.LogWarning(
            EventIds.SupervisorCrashDetected,
            "Health check: agent {AgentId} container {ContainerId} is not running (restart #{RestartCount})",
            state.AgentId, state.ContainerId, state.RestartCount + 1);

        var crashed = state with
        {
            Status = ContainerSupervisionStatus.CrashDetected,
            LastCrashAt = DateTimeOffset.UtcNow,
        };
        await SaveStateAsync(crashed, cancellationToken);

        await RestartAsync(crashed, cancellationToken);
    }

    /// <summary>
    /// Dapr reminder callback — drives the periodic health-check poll.
    /// </summary>
    public async Task ReceiveReminderAsync(
        string reminderName,
        byte[] state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        if (!string.Equals(reminderName, HealthCheckReminderName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await CheckHealthAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                EventIds.SupervisorHealthCheckFailed,
                ex,
                "Health-check reminder for agent {AgentId} threw; will retry at next interval",
                Id.GetId());
        }
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private async Task RestartAsync(SupervisorState state, CancellationToken cancellationToken)
    {
        if (state.RestartCount >= state.MaxRestarts)
        {
            _logger.LogError(
                EventIds.SupervisorGaveUp,
                "Supervisor for agent {AgentId} has reached the restart limit ({MaxRestarts}); marking Failed",
                state.AgentId, state.MaxRestarts);

            var failed = state with { Status = ContainerSupervisionStatus.Failed, ContainerId = null };
            await SaveStateAsync(failed, cancellationToken);
            await TryUnregisterReminderAsync();
            return;
        }

        _logger.LogInformation(
            EventIds.SupervisorRestarting,
            "Supervisor for agent {AgentId} restarting container (attempt {Attempt}/{MaxRestarts}). " +
            "Workspace volume {VolumeName} is preserved so initialize() sees existing checkpoint state.",
            state.AgentId, state.RestartCount + 1, state.MaxRestarts, state.VolumeName);

        // Stop the crashed container (best-effort — it may already be gone).
        if (state.ContainerId is not null)
        {
            try
            {
                await containerRuntime.StopAsync(state.ContainerId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not stop crashed container {ContainerId} for agent {AgentId}; proceeding with restart",
                    state.ContainerId, state.AgentId);
            }
        }

        // The workspace volume already exists (EnsureAsync is idempotent) and
        // survived the crash per ADR-0029 § 3.2. The restarted container's
        // initialize() will inspect it and decide whether to resume or start fresh.
        var volumeMount = state.VolumeName is not null
            ? AgentVolumeManager.BuildVolumeMount(state.VolumeName)
            : null;

        // Rebuild the container config using the persisted image. Environment
        // variables (including scoped credentials) are not re-injected on
        // automatic restart — they would have expired. A restart that needs
        // fresh credentials requires an explicit Stop + StartAsync cycle.
        // The workspace volume mount is re-applied so initialize() sees the
        // existing checkpoint state (ADR-0029 § 3.2 / 3.3).
        if (state.Image is null)
        {
            _logger.LogError(
                EventIds.SupervisorRestartFailed,
                "Supervisor for agent {AgentId} cannot restart — image not persisted in state. " +
                "Call StartAsync again to supply a new image.",
                state.AgentId);
            await SaveStateAsync(state with { Status = ContainerSupervisionStatus.Failed }, cancellationToken);
            await TryUnregisterReminderAsync();
            return;
        }

        var config = new ContainerConfig(
            Image: state.Image,
            VolumeMounts: volumeMount is not null ? [volumeMount] : null,
            NetworkName: state.NetworkName);

        string newContainerId;
        try
        {
            newContainerId = await containerRuntime.StartAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                EventIds.SupervisorRestartFailed,
                ex,
                "Supervisor for agent {AgentId} failed to restart container; will retry at next health-check",
                state.AgentId);

            // Revert to CrashDetected so the next poll retries.
            await SaveStateAsync(state with { Status = ContainerSupervisionStatus.CrashDetected }, cancellationToken);
            return;
        }

        var restarted = state with
        {
            Status = ContainerSupervisionStatus.Running,
            ContainerId = newContainerId,
            RestartCount = state.RestartCount + 1,
            LastStartedAt = DateTimeOffset.UtcNow,
        };
        await SaveStateAsync(restarted, cancellationToken);

        _logger.LogInformation(
            EventIds.SupervisorRestarted,
            "Supervisor for agent {AgentId} restarted successfully as container {ContainerId} (restart #{RestartCount})",
            state.AgentId, newContainerId, restarted.RestartCount);
    }

    private async Task<bool> IsContainerAliveAsync(string containerId, CancellationToken cancellationToken)
    {
        // Issue a lightweight HTTP probe from the host to the container's
        // A2A Agent Card endpoint. The host-side probe resolves the
        // container's bridge IP via `podman inspect` and issues a plain
        // HTTP GET from the dispatcher process — no `podman exec`, no
        // in-container binary (`wget`, `curl`) required. This is the
        // canonical post-#1175 readiness probe for any agent container
        // regardless of base image.
        //
        // Note: ProbeHttpFromHostAsync returns false for non-2xx responses
        // and for inspect / network errors, so we cannot distinguish
        // "running but not ready" from "crashed." We treat the latter as a
        // transient restart candidate — the restart is idempotent and the
        // workspace volume ensures state continuity.
        try
        {
            // Probe the well-known A2A agent-card endpoint.
            var alive = await containerRuntime.ProbeHttpFromHostAsync(
                containerId,
                "http://localhost:8999/.well-known/agent.json",
                cancellationToken);
            return alive;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "ProbeHttpFromHostAsync for container {ContainerId} threw; treating as not alive",
                containerId);
            return false;
        }
    }

    private async Task StopContainerAsync(SupervisorState state, CancellationToken cancellationToken)
    {
        if (state.ContainerId is null)
        {
            return;
        }

        try
        {
            if (state.SidecarId is not null && state.NetworkName is not null)
            {
                await containerLifecycleManager.TeardownAsync(
                    state.ContainerId,
                    state.SidecarId,
                    state.NetworkName,
                    cancellationToken);
            }
            else
            {
                await containerRuntime.StopAsync(state.ContainerId, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to stop container {ContainerId} for agent {AgentId}; may need manual cleanup",
                state.ContainerId, state.AgentId);
        }
    }

    private async Task<SupervisorState?> TryGetStateAsync(CancellationToken cancellationToken)
    {
        var result = await StateManager.TryGetStateAsync<SupervisorState>(
            SupervisorStateKey, cancellationToken);
        return result.HasValue ? result.Value : null;
    }

    private Task SaveStateAsync(SupervisorState state, CancellationToken cancellationToken)
        => StateManager.SetStateAsync(SupervisorStateKey, state, cancellationToken);

    private async Task TryUnregisterReminderAsync()
    {
        try
        {
            await UnregisterReminderAsync(HealthCheckReminderName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Could not unregister reminder {ReminderName} for actor {ActorId}; may already be gone",
                HealthCheckReminderName, Id.GetId());
        }
    }

    /// <summary>
    /// Merges the workspace-path env var into the caller's environment variables.
    /// The D1 spec (§ 2.2.1) mandates <c>SPRING_WORKSPACE_PATH</c> be set before
    /// the container's main process starts.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildEnvironmentVariables(
        IReadOnlyDictionary<string, string>? callerEnv)
    {
        var env = callerEnv is not null
            ? new Dictionary<string, string>(callerEnv)
            : new Dictionary<string, string>();

        // Ensure the workspace path env var is always present, pointing at the
        // canonical mount path. The caller may already have supplied it; we do
        // not override a caller-supplied value so tests can redirect the path.
        env.TryAdd(AgentVolumeManager.WorkspacePathEnvVar, AgentVolumeManager.WorkspaceMountPath);

        return env;
    }

    /// <summary>
    /// Event IDs for container supervisor actor logging (range 2000–2099
    /// per CONVENTIONS.md § 9, Cvoya.Spring.Dapr.Actors).
    /// </summary>
    private static class EventIds
    {
        public static readonly EventId SupervisorStartIdempotent = new(2050, nameof(SupervisorStartIdempotent));
        public static readonly EventId SupervisorStarting = new(2051, nameof(SupervisorStarting));
        public static readonly EventId SupervisorStarted = new(2052, nameof(SupervisorStarted));
        public static readonly EventId SupervisorDone = new(2053, nameof(SupervisorDone));
        public static readonly EventId SupervisorStopping = new(2054, nameof(SupervisorStopping));
        public static readonly EventId SupervisorStopped = new(2055, nameof(SupervisorStopped));
        public static readonly EventId SupervisorHealthOk = new(2056, nameof(SupervisorHealthOk));
        public static readonly EventId SupervisorCrashDetected = new(2057, nameof(SupervisorCrashDetected));
        public static readonly EventId SupervisorRestarting = new(2058, nameof(SupervisorRestarting));
        public static readonly EventId SupervisorRestarted = new(2059, nameof(SupervisorRestarted));
        public static readonly EventId SupervisorRestartFailed = new(2060, nameof(SupervisorRestartFailed));
        public static readonly EventId SupervisorGaveUp = new(2061, nameof(SupervisorGaveUp));
        public static readonly EventId SupervisorHealthCheckFailed = new(2062, nameof(SupervisorHealthCheckFailed));
    }
}