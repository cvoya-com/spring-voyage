// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Linq;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ContainerLifecycleManager"/>.
/// </summary>
public class ContainerLifecycleManagerTests
{
    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly IDaprSidecarManager _sidecarManager = Substitute.For<IDaprSidecarManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly ContainerLifecycleManager _manager;
    private readonly DaprSidecarOptions _sidecarOptions;

    public ContainerLifecycleManagerTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        // Stage 2 of #522: ContainerLifecycleManager dropped its
        // ContainerRuntimeOptions dependency (network ops now route through
        // IContainerRuntime). The only options it still reads is the Dapr
        // sidecar's components-path knob, which moved to DaprSidecarOptions.
        _sidecarOptions = new DaprSidecarOptions();
        _manager = new ContainerLifecycleManager(
            _containerRuntime,
            _sidecarManager,
            Options.Create(_sidecarOptions),
            _loggerFactory);
    }

    [Fact]
    public async Task TeardownAsync_StopsContainerAndSidecar()
    {
        await _manager.TeardownAsync("app-container", "sidecar-container", "test-network", TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).StopAsync("app-container", Arg.Any<CancellationToken>());
        await _sidecarManager.Received(1).StopSidecarAsync("sidecar-container", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TeardownAsync_SkipsNullContainerId()
    {
        await _manager.TeardownAsync(null, "sidecar-container", "test-network", TestContext.Current.CancellationToken);

        await _containerRuntime.DidNotReceive().StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sidecarManager.Received(1).StopSidecarAsync("sidecar-container", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TeardownAsync_SkipsNullSidecarId()
    {
        await _manager.TeardownAsync("app-container", null, "test-network", TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).StopAsync("app-container", Arg.Any<CancellationToken>());
        await _sidecarManager.DidNotReceive().StopSidecarAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TeardownAsync_ContinuesOnContainerStopFailure()
    {
        _containerRuntime.StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("stop failed"));

        // Should not throw; sidecar cleanup should still happen.
        await _manager.TeardownAsync("app-container", "sidecar-container", "test-network", TestContext.Current.CancellationToken);

        await _sidecarManager.Received(1).StopSidecarAsync("sidecar-container", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TeardownAsync_ContinuesOnSidecarStopFailure()
    {
        _sidecarManager.StopSidecarAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("sidecar stop failed"));

        // Should not throw.
        var act = () => _manager.TeardownAsync("app-container", "sidecar-container", "test-network", TestContext.Current.CancellationToken);
        await Should.NotThrowAsync(act);
    }

    [Fact]
    public async Task TeardownAsync_AllNull_DoesNothing()
    {
        await _manager.TeardownAsync(null, null, null, TestContext.Current.CancellationToken);

        await _containerRuntime.DidNotReceive().StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sidecarManager.DidNotReceive().StopSidecarAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LaunchWithSidecarAsync_DualAttachesAppContainerToTenantNetwork()
    {
        // Sidecar is healthy on the first probe so the lifecycle proceeds to
        // the app run. We capture the augmented ContainerConfig handed to
        // the runtime to assert tenant-network dual-attach (#1166 / ADR 0028).
        StubSidecarHappyPath();

        ContainerConfig? launchedConfig = null;
        _containerRuntime.RunAsync(Arg.Do<ContainerConfig>(c => launchedConfig = c), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("app-container", 0, "ok", string.Empty));

        var input = new ContainerConfig(
            Image: "agent:v1",
            DaprEnabled: true,
            DaprAppId: "spring-app-test",
            DaprAppPort: 8080);

        await _manager.LaunchWithSidecarAsync(input, TestContext.Current.CancellationToken);

        launchedConfig.ShouldNotBeNull();
        launchedConfig!.NetworkName.ShouldNotBeNull();
        launchedConfig.NetworkName.ShouldStartWith("spring-net-");
        launchedConfig.AdditionalNetworks.ShouldNotBeNull();
        launchedConfig.AdditionalNetworks!.ShouldContain(ContainerConfigBuilder.TenantNetworkName);
    }

    [Fact]
    public async Task LaunchWithSidecarAsync_SetsDaprEndpointsToSidecarContainerName()
    {
        // App and daprd are two containers: clients must not default to
        // 127.0.0.1:50001 (Durable Task / dapr-agents gRPC to the sidecar).
        StubSidecarHappyPath();

        ContainerConfig? launchedConfig = null;
        _containerRuntime.RunAsync(Arg.Do<ContainerConfig>(c => launchedConfig = c), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("app-container", 0, "ok", string.Empty));

        var input = new ContainerConfig(
            Image: "agent:v1",
            DaprEnabled: true,
            DaprAppId: "spring-app-test",
            DaprAppPort: 8080);

        await _manager.LaunchWithSidecarAsync(input, TestContext.Current.CancellationToken);

        launchedConfig.ShouldNotBeNull();
        var env = launchedConfig!.EnvironmentVariables;
        env.ShouldNotBeNull();
        env!["DAPR_HTTP_ENDPOINT"].ShouldBe("http://sidecar-1:3500");
        env!["DAPR_GRPC_ENDPOINT"].ShouldBe("http://sidecar-1:50001");
    }

    [Fact]
    public async Task LaunchWithSidecarAsync_KeepsSidecarOnPerWorkflowBridgeOnly()
    {
        // The sidecar is daprd, which talks to the app over the per-workflow
        // bridge — it has no business reaching tenant infrastructure. Pin
        // that here so a future change that refactors the launch path
        // doesn't accidentally widen the sidecar's network footprint.
        DaprSidecarConfig? capturedSidecarConfig = null;
        _sidecarManager.StartSidecarAsync(
                Arg.Do<DaprSidecarConfig>(c => capturedSidecarConfig = c),
                Arg.Any<CancellationToken>())
            .Returns(new DaprSidecarInfo("sidecar-1", 3500, 50001, "spring-net-test"));
        _sidecarManager.WaitForHealthyAsync(Arg.Any<DaprSidecarInfo>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("app-container", 0, string.Empty, string.Empty));

        var input = new ContainerConfig(Image: "agent:v1", DaprEnabled: true);

        await _manager.LaunchWithSidecarAsync(input, TestContext.Current.CancellationToken);

        capturedSidecarConfig.ShouldNotBeNull();
        capturedSidecarConfig!.NetworkName.ShouldNotBeNull();
        capturedSidecarConfig.NetworkName.ShouldStartWith("spring-net-");
        capturedSidecarConfig.NetworkName.ShouldNotBe(ContainerConfigBuilder.TenantNetworkName);
    }

    [Fact]
    public async Task LaunchWithSidecarAsync_EnsuresTenantNetworkExistsBeforeLaunch()
    {
        // ADR 0028: the lifecycle must be self-sufficient — a fresh clone or
        // partial bring-up must still launch successfully even if deploy.sh
        // has not been re-run. We assert the tenant network is created
        // (idempotently) alongside the per-workflow bridge.
        StubSidecarHappyPath();

        var createdNetworks = new List<string>();
        _containerRuntime.CreateNetworkAsync(
                Arg.Do<string>(n => createdNetworks.Add(n)),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("app-container", 0, string.Empty, string.Empty));

        var input = new ContainerConfig(Image: "agent:v1", DaprEnabled: true);

        await _manager.LaunchWithSidecarAsync(input, TestContext.Current.CancellationToken);

        createdNetworks.ShouldContain(ContainerConfigBuilder.TenantNetworkName);
        // The per-workflow ephemeral bridge is also created — the sidecar runs
        // there, the app dual-attaches to it plus the tenant bridge.
        createdNetworks.Count(n => n.StartsWith("spring-net-")).ShouldBe(1);
    }

    [Fact]
    public async Task LaunchWithSidecarAsync_DeduplicatesTenantNetworkWhenCallerAlreadyPinnedIt()
    {
        // A future caller might already enumerate the tenant network in
        // AdditionalNetworks. Don't emit a duplicate `--network` flag.
        StubSidecarHappyPath();

        ContainerConfig? launchedConfig = null;
        _containerRuntime.RunAsync(Arg.Do<ContainerConfig>(c => launchedConfig = c), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("app-container", 0, string.Empty, string.Empty));

        var input = new ContainerConfig(
            Image: "agent:v1",
            DaprEnabled: true,
            AdditionalNetworks: [ContainerConfigBuilder.TenantNetworkName]);

        await _manager.LaunchWithSidecarAsync(input, TestContext.Current.CancellationToken);

        launchedConfig.ShouldNotBeNull();
        launchedConfig!.AdditionalNetworks.ShouldNotBeNull();
        launchedConfig.AdditionalNetworks!.Count(n => n == ContainerConfigBuilder.TenantNetworkName).ShouldBe(1);
    }

    private void StubSidecarHappyPath()
    {
        _sidecarManager.StartSidecarAsync(Arg.Any<DaprSidecarConfig>(), Arg.Any<CancellationToken>())
            .Returns(new DaprSidecarInfo("sidecar-1", 3500, 50001, "spring-net-test"));
        _sidecarManager.WaitForHealthyAsync(Arg.Any<DaprSidecarInfo>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }
}