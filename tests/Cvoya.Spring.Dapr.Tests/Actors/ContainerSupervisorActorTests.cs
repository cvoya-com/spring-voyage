// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Execution;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ContainerSupervisorActor"/> covering start,
/// crash detection, restart, reclaim-on-done, and ephemeral vs persistent paths.
/// (D3d — ADR-0029 § "Failure recovery")
/// </summary>
public class ContainerSupervisorActorTests
{
    private const string TestAgentId = "test-agent-123";

    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly ActorTimerManager _timerManager = Substitute.For<ActorTimerManager>();
    private readonly ContainerLifecycleManager _lifecycleManager;
    private readonly AgentVolumeManager _volumeManager;
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly ContainerSupervisorActor _actor;

    public ContainerSupervisorActorTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var sidecarManager = Substitute.For<IDaprSidecarManager>();
        var sidecarOptions = Microsoft.Extensions.Options.Options.Create(new DaprSidecarOptions());
        _lifecycleManager = new ContainerLifecycleManager(
            _containerRuntime, sidecarManager, sidecarOptions, _loggerFactory);

        _volumeManager = new AgentVolumeManager(_containerRuntime, _loggerFactory);

        var host = ActorHost.CreateForTest<ContainerSupervisorActor>(new ActorTestOptions
        {
            ActorId = new ActorId(TestAgentId),
            TimerManager = _timerManager,
        });

        _actor = new ContainerSupervisorActor(
            host,
            _containerRuntime,
            _lifecycleManager,
            _volumeManager,
            _loggerFactory);

        SetStateManager(_actor, _stateManager);

        // Default: no persisted state.
        _stateManager
            .TryGetStateAsync<SupervisorState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<SupervisorState>(false, default!));
    }

    // ------------------------------------------------------------------
    // StartAsync — happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_NoExistingContainer_ProvisionesVolumeAndStartsContainer()
    {
        const string expectedContainerId = "container-abc";
        const string expectedVolumeName = "spring-ws-test-agent-123";

        _containerRuntime
            .EnsureVolumeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _containerRuntime
            .StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(expectedContainerId);

        var request = new SupervisorLaunchRequest(
            AgentId: TestAgentId,
            Image: "ghcr.io/cvoya/test-agent:1.0.0",
            Hosting: AgentHostingMode.Ephemeral);

        var containerId = await _actor.StartAsync(request, CancellationToken.None);

        containerId.ShouldBe(expectedContainerId);

        // Volume provisioned.
        await _containerRuntime.Received(1).EnsureVolumeAsync(
            expectedVolumeName,
            Arg.Any<CancellationToken>());

        // Container started with the workspace volume mount.
        await _containerRuntime.Received(1).StartAsync(
            Arg.Is<ContainerConfig>(c =>
                c.Image == "ghcr.io/cvoya/test-agent:1.0.0" &&
                c.VolumeMounts != null &&
                c.VolumeMounts.Any(m => m.Contains(expectedVolumeName))),
            Arg.Any<CancellationToken>());

        // State persisted.
        await _stateManager.Received(1).SetStateAsync(
            Arg.Any<string>(),
            Arg.Is<SupervisorState>(s =>
                s.AgentId == TestAgentId &&
                s.Status == ContainerSupervisionStatus.Running &&
                s.ContainerId == expectedContainerId &&
                s.VolumeName == expectedVolumeName),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_ContainerEnvVars_IncludesWorkspacePathEnvVar()
    {
        _containerRuntime
            .EnsureVolumeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _containerRuntime
            .StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns("container-xyz");

        var callerEnv = new Dictionary<string, string>
        {
            ["SPRING_AGENT_ID"] = TestAgentId,
            ["SPRING_TENANT_ID"] = "tenant-acme",
        };

        var request = new SupervisorLaunchRequest(
            AgentId: TestAgentId,
            Image: "ghcr.io/cvoya/test-agent:1.0.0",
            EnvironmentVariables: callerEnv,
            Hosting: AgentHostingMode.Persistent);

        await _actor.StartAsync(request, CancellationToken.None);

        await _containerRuntime.Received(1).StartAsync(
            Arg.Is<ContainerConfig>(c =>
                c.EnvironmentVariables != null &&
                c.EnvironmentVariables.ContainsKey(AgentVolumeManager.WorkspacePathEnvVar) &&
                c.EnvironmentVariables[AgentVolumeManager.WorkspacePathEnvVar] == AgentVolumeManager.WorkspaceMountPath &&
                c.EnvironmentVariables.ContainsKey("SPRING_AGENT_ID")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_AlreadyRunning_ReturnsExistingContainerIdWithoutRestart()
    {
        const string existingContainerId = "already-running-container";

        var existingState = new SupervisorState(
            AgentId: TestAgentId,
            Hosting: AgentHostingMode.Persistent,
            Status: ContainerSupervisionStatus.Running,
            ContainerId: existingContainerId,
            SidecarId: null,
            NetworkName: null,
            VolumeName: "spring-ws-test-agent-123",
            RestartCount: 0,
            MaxRestarts: ContainerSupervisorActor.DefaultMaxRestarts,
            LastStartedAt: DateTimeOffset.UtcNow,
            LastCrashAt: null);

        _stateManager
            .TryGetStateAsync<SupervisorState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<SupervisorState>(true, existingState));

        var request = new SupervisorLaunchRequest(
            AgentId: TestAgentId,
            Image: "ghcr.io/cvoya/test-agent:1.0.0");

        var containerId = await _actor.StartAsync(request, CancellationToken.None);

        containerId.ShouldBe(existingContainerId);

        // No new container should have been started.
        await _containerRuntime.DidNotReceive().StartAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // DoneAsync — ephemeral completion with volume reclaim
    // ------------------------------------------------------------------

    [Fact]
    public async Task DoneAsync_EphemeralAgent_StopsContainerAndReclaimsVolume()
    {
        const string containerId = "ephemeral-container";
        const string volumeName = "spring-ws-test-agent-123";

        var runningState = new SupervisorState(
            AgentId: TestAgentId,
            Hosting: AgentHostingMode.Ephemeral,
            Status: ContainerSupervisionStatus.Running,
            ContainerId: containerId,
            SidecarId: null,
            NetworkName: null,
            VolumeName: volumeName,
            RestartCount: 0,
            MaxRestarts: ContainerSupervisorActor.DefaultMaxRestarts,
            LastStartedAt: DateTimeOffset.UtcNow,
            LastCrashAt: null);

        _stateManager
            .TryGetStateAsync<SupervisorState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<SupervisorState>(true, runningState));

        await _actor.DoneAsync(CancellationToken.None);

        // Container stopped.
        await _containerRuntime.Received(1).StopAsync(containerId, Arg.Any<CancellationToken>());

        // Volume reclaimed — ephemeral path requires reclamation on completion.
        await _containerRuntime.Received(1).RemoveVolumeAsync(volumeName, Arg.Any<CancellationToken>());

        // State updated to Stopped.
        await _stateManager.Received(1).SetStateAsync(
            Arg.Any<string>(),
            Arg.Is<SupervisorState>(s => s.Status == ContainerSupervisionStatus.Stopped),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DoneAsync_PersistentAgent_StopsContainerButDoesNotReclaimVolume()
    {
        const string containerId = "persistent-container";
        const string volumeName = "spring-ws-test-agent-123";

        var runningState = new SupervisorState(
            AgentId: TestAgentId,
            Hosting: AgentHostingMode.Persistent,
            Status: ContainerSupervisionStatus.Running,
            ContainerId: containerId,
            SidecarId: null,
            NetworkName: null,
            VolumeName: volumeName,
            RestartCount: 0,
            MaxRestarts: ContainerSupervisorActor.DefaultMaxRestarts,
            LastStartedAt: DateTimeOffset.UtcNow,
            LastCrashAt: null);

        _stateManager
            .TryGetStateAsync<SupervisorState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<SupervisorState>(true, runningState));

        await _actor.DoneAsync(CancellationToken.None);

        // Container stopped.
        await _containerRuntime.Received(1).StopAsync(containerId, Arg.Any<CancellationToken>());

        // Volume NOT reclaimed — persistent agents keep their workspace.
        await _containerRuntime.DidNotReceive().RemoveVolumeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DoneAsync_NoContainer_IsIdempotent()
    {
        // No state persisted at all.
        await _actor.DoneAsync(CancellationToken.None);

        await _containerRuntime.DidNotReceive().StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _containerRuntime.DidNotReceive().RemoveVolumeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // StopAsync — persistent undeploy without volume reclaim
    // ------------------------------------------------------------------

    [Fact]
    public async Task StopAsync_PersistentAgent_StopsContainerWithoutReclaimingVolume()
    {
        const string containerId = "persistent-container";
        const string volumeName = "spring-ws-test-agent-123";

        var runningState = new SupervisorState(
            AgentId: TestAgentId,
            Hosting: AgentHostingMode.Persistent,
            Status: ContainerSupervisionStatus.Running,
            ContainerId: containerId,
            SidecarId: null,
            NetworkName: null,
            VolumeName: volumeName,
            RestartCount: 0,
            MaxRestarts: ContainerSupervisorActor.DefaultMaxRestarts,
            LastStartedAt: DateTimeOffset.UtcNow,
            LastCrashAt: null);

        _stateManager
            .TryGetStateAsync<SupervisorState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<SupervisorState>(true, runningState));

        await _actor.StopAsync(CancellationToken.None);

        await _containerRuntime.Received(1).StopAsync(containerId, Arg.Any<CancellationToken>());
        await _containerRuntime.DidNotReceive().RemoveVolumeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        await _stateManager.Received(1).SetStateAsync(
            Arg.Any<string>(),
            Arg.Is<SupervisorState>(s => s.Status == ContainerSupervisionStatus.Stopped),
            Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // CheckHealthAsync — crash detection and restart
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckHealthAsync_ContainerAlive_DoesNotRestart()
    {
        const string containerId = "healthy-container";

        var runningState = new SupervisorState(
            AgentId: TestAgentId,
            Hosting: AgentHostingMode.Persistent,
            Status: ContainerSupervisionStatus.Running,
            ContainerId: containerId,
            SidecarId: null,
            NetworkName: null,
            VolumeName: "spring-ws-test-agent-123",
            RestartCount: 0,
            MaxRestarts: ContainerSupervisorActor.DefaultMaxRestarts,
            LastStartedAt: DateTimeOffset.UtcNow,
            LastCrashAt: null);

        _stateManager
            .TryGetStateAsync<SupervisorState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<SupervisorState>(true, runningState));

        _containerRuntime
            .ProbeContainerHttpAsync(containerId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await _actor.CheckHealthAsync(CancellationToken.None);

        // No restart attempted.
        await _containerRuntime.DidNotReceive().StartAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckHealthAsync_ContainerCrashed_DetectsCrash()
    {
        const string containerId = "crashed-container";

        var runningState = new SupervisorState(
            AgentId: TestAgentId,
            Hosting: AgentHostingMode.Persistent,
            Status: ContainerSupervisionStatus.Running,
            ContainerId: containerId,
            SidecarId: null,
            NetworkName: null,
            VolumeName: "spring-ws-test-agent-123",
            RestartCount: 0,
            MaxRestarts: ContainerSupervisorActor.DefaultMaxRestarts,
            LastStartedAt: DateTimeOffset.UtcNow,
            LastCrashAt: null);

        _stateManager
            .TryGetStateAsync<SupervisorState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<SupervisorState>(true, runningState));

        // Container is not alive.
        _containerRuntime
            .ProbeContainerHttpAsync(containerId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await _actor.CheckHealthAsync(CancellationToken.None);

        // The state should have been updated to CrashDetected (then attempted restart).
        await _stateManager.Received().SetStateAsync(
            Arg.Any<string>(),
            Arg.Is<SupervisorState>(s =>
                s.Status == ContainerSupervisionStatus.CrashDetected ||
                s.Status == ContainerSupervisionStatus.Running),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckHealthAsync_WorkspaceVolumePreservedOnRestart()
    {
        // The workspace volume must survive the crash so the restarted container
        // can resume from checkpoint state (ADR-0029 § 3.2 + D3c).
        const string containerId = "crashed-container";
        const string volumeName = "spring-ws-test-agent-123";

        var runningState = new SupervisorState(
            AgentId: TestAgentId,
            Hosting: AgentHostingMode.Persistent,
            Status: ContainerSupervisionStatus.Running,
            ContainerId: containerId,
            SidecarId: null,
            NetworkName: null,
            VolumeName: volumeName,
            RestartCount: 0,
            MaxRestarts: ContainerSupervisorActor.DefaultMaxRestarts,
            LastStartedAt: DateTimeOffset.UtcNow,
            LastCrashAt: null);

        _stateManager
            .TryGetStateAsync<SupervisorState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<SupervisorState>(true, runningState));

        _containerRuntime
            .ProbeContainerHttpAsync(containerId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await _actor.CheckHealthAsync(CancellationToken.None);

        // Volume must NOT have been reclaimed during crash handling.
        await _containerRuntime.DidNotReceive().RemoveVolumeAsync(
            volumeName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckHealthAsync_ExceedsRestartLimit_MarksAsFailed()
    {
        const string containerId = "doomed-container";

        var crashedState = new SupervisorState(
            AgentId: TestAgentId,
            Hosting: AgentHostingMode.Persistent,
            Status: ContainerSupervisionStatus.Running,
            ContainerId: containerId,
            SidecarId: null,
            NetworkName: null,
            VolumeName: "spring-ws-test-agent-123",
            RestartCount: ContainerSupervisorActor.DefaultMaxRestarts, // already at limit
            MaxRestarts: ContainerSupervisorActor.DefaultMaxRestarts,
            LastStartedAt: DateTimeOffset.UtcNow,
            LastCrashAt: null);

        _stateManager
            .TryGetStateAsync<SupervisorState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<SupervisorState>(true, crashedState));

        _containerRuntime
            .ProbeContainerHttpAsync(containerId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await _actor.CheckHealthAsync(CancellationToken.None);

        // Should be marked as Failed, not restarted.
        await _stateManager.Received(1).SetStateAsync(
            Arg.Any<string>(),
            Arg.Is<SupervisorState>(s => s.Status == ContainerSupervisionStatus.Failed),
            Arg.Any<CancellationToken>());

        await _containerRuntime.DidNotReceive().StartAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckHealthAsync_ContainerCrashedWithPersistedImage_RestartsUsingSameImage()
    {
        // The supervisor persists the image on StartAsync so it can self-heal
        // on a crash without a new dispatcher call.
        const string persistedImage = "ghcr.io/cvoya/test-agent:1.0.0";
        const string crashedContainerId = "crashed-container";
        const string newContainerId = "restarted-container";
        const string volumeName = "spring-ws-test-agent-123";

        var runningState = new SupervisorState(
            AgentId: TestAgentId,
            Hosting: AgentHostingMode.Persistent,
            Status: ContainerSupervisionStatus.Running,
            ContainerId: crashedContainerId,
            SidecarId: null,
            NetworkName: null,
            VolumeName: volumeName,
            RestartCount: 0,
            MaxRestarts: ContainerSupervisorActor.DefaultMaxRestarts,
            LastStartedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            LastCrashAt: null,
            Image: persistedImage);

        _stateManager
            .TryGetStateAsync<SupervisorState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<SupervisorState>(true, runningState));

        _containerRuntime
            .ProbeContainerHttpAsync(crashedContainerId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _containerRuntime
            .StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(newContainerId);

        await _actor.CheckHealthAsync(TestContext.Current.CancellationToken);

        // Container restarted with the persisted image.
        await _containerRuntime.Received(1).StartAsync(
            Arg.Is<ContainerConfig>(c =>
                c.Image == persistedImage &&
                c.VolumeMounts != null &&
                c.VolumeMounts.Any(m => m.Contains(volumeName))),
            Arg.Any<CancellationToken>());

        // Volume NOT reclaimed during restart (must survive for checkpoint resume).
        await _containerRuntime.DidNotReceive().RemoveVolumeAsync(
            volumeName, Arg.Any<CancellationToken>());

        // Restart count incremented.
        await _stateManager.Received(1).SetStateAsync(
            Arg.Any<string>(),
            Arg.Is<SupervisorState>(s =>
                s.Status == ContainerSupervisionStatus.Running &&
                s.ContainerId == newContainerId &&
                s.RestartCount == 1),
            Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------
    // GetStateAsync — initial state when no container started
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStateAsync_NoPriorState_ReturnsIdleState()
    {
        var state = await _actor.GetStateAsync(CancellationToken.None);

        state.AgentId.ShouldBe(TestAgentId);
        state.Status.ShouldBe(ContainerSupervisionStatus.Idle);
        state.ContainerId.ShouldBeNull();
    }

    [Fact]
    public async Task GetStateAsync_AfterStart_ReturnsRunningState()
    {
        const string containerId = "running-container";

        var runningState = new SupervisorState(
            AgentId: TestAgentId,
            Hosting: AgentHostingMode.Ephemeral,
            Status: ContainerSupervisionStatus.Running,
            ContainerId: containerId,
            SidecarId: null,
            NetworkName: null,
            VolumeName: "spring-ws-test-agent-123",
            RestartCount: 0,
            MaxRestarts: ContainerSupervisorActor.DefaultMaxRestarts,
            LastStartedAt: DateTimeOffset.UtcNow,
            LastCrashAt: null);

        _stateManager
            .TryGetStateAsync<SupervisorState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<SupervisorState>(true, runningState));

        var state = await _actor.GetStateAsync(CancellationToken.None);

        state.Status.ShouldBe(ContainerSupervisionStatus.Running);
        state.ContainerId.ShouldBe(containerId);
    }

    // ------------------------------------------------------------------
    // Reminder callback
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReceiveReminderAsync_HealthCheckReminder_TriggersCheckHealth()
    {
        // Container is alive — reminder fires without restarting.
        var runningState = new SupervisorState(
            AgentId: TestAgentId,
            Hosting: AgentHostingMode.Persistent,
            Status: ContainerSupervisionStatus.Running,
            ContainerId: "healthy-container",
            SidecarId: null,
            NetworkName: null,
            VolumeName: "spring-ws-test-agent-123",
            RestartCount: 0,
            MaxRestarts: ContainerSupervisorActor.DefaultMaxRestarts,
            LastStartedAt: DateTimeOffset.UtcNow,
            LastCrashAt: null);

        _stateManager
            .TryGetStateAsync<SupervisorState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<SupervisorState>(true, runningState));

        _containerRuntime
            .ProbeContainerHttpAsync("healthy-container", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await _actor.ReceiveReminderAsync(
            ContainerSupervisorActor.HealthCheckReminderName,
            [],
            TimeSpan.Zero,
            ContainerSupervisorActor.HealthCheckInterval);

        // Health was probed — confirms the reminder drives the check loop.
        await _containerRuntime.Received(1).ProbeContainerHttpAsync(
            "healthy-container", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReceiveReminderAsync_UnknownReminder_IsIgnored()
    {
        // Should not throw or interact with the runtime.
        await _actor.ReceiveReminderAsync(
            "some-unrecognised-reminder",
            [],
            TimeSpan.Zero,
            TimeSpan.FromMinutes(1));

        await _containerRuntime.DidNotReceiveWithAnyArgs()
            .ProbeContainerHttpAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void SetStateManager(Actor actor, IActorStateManager stateManager)
    {
        var field = typeof(Actor).GetField(
            "<StateManager>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (field is not null)
        {
            field.SetValue(actor, stateManager);
            return;
        }

        var prop = typeof(Actor).GetProperty("StateManager");
        prop?.SetValue(actor, stateManager);
    }
}