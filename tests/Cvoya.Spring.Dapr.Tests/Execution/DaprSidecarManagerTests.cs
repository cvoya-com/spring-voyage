// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DaprSidecarManager"/>.
/// </summary>
/// <remarks>
/// Stage 2 of #522 / #1063 rewrote this class to route every container
/// operation through <see cref="IContainerRuntime"/>. The previous test
/// surface (a static <c>BuildSidecarRunArguments</c> string builder) is
/// gone; we now assert on the <see cref="ContainerConfig"/> shape that
/// <see cref="DaprSidecarManager.BuildSidecarContainerConfig"/> emits, plus
/// the runtime-call shape <c>StartSidecarAsync</c> / <c>StopSidecarAsync</c>
/// / <c>WaitForHealthyAsync</c> hand to <see cref="IContainerRuntime"/>.
/// </remarks>
public class DaprSidecarManagerTests
{
    private static DaprSidecarManager CreateManager(
        IContainerRuntime? runtime = null,
        DaprSidecarOptions? options = null)
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        return new DaprSidecarManager(
            runtime ?? Substitute.For<IContainerRuntime>(),
            Options.Create(options ?? new DaprSidecarOptions()),
            loggerFactory);
    }

    [Fact]
    public void BuildSidecarContainerConfig_MinimalConfig_ProducesExpectedShape()
    {
        var manager = CreateManager();
        var config = new DaprSidecarConfig(
            AppId: "my-app",
            AppPort: 8080,
            DaprHttpPort: 3500,
            DaprGrpcPort: 50001);

        var containerConfig = manager.BuildSidecarContainerConfig(config, "spring-dapr-test");

        containerConfig.Image.ShouldBe("daprio/daprd:1.17.4");
        containerConfig.Command.ShouldNotBeNull();
        containerConfig.Command![0].ShouldBe("./daprd");
        containerConfig.Command.ShouldBe([
            "./daprd",
            "--app-id", "my-app",
            "--app-port", "8080",
            "--dapr-http-port", "3500",
            "--dapr-grpc-port", "50001",
        ]);
        containerConfig.Command.ShouldNotContain("--resources-path");
        containerConfig.NetworkName.ShouldBeNull();
        containerConfig.VolumeMounts.ShouldBeNull();
        containerConfig.Labels.ShouldNotBeNull();
        containerConfig.Labels!["spring.managed"].ShouldBe("true");
        containerConfig.Labels["spring.role"].ShouldBe("dapr-sidecar");
        containerConfig.Labels["spring.app-id"].ShouldBe("my-app");
    }

    [Fact]
    public void BuildSidecarContainerConfig_WithNetwork_PropagatesNetworkName()
    {
        var manager = CreateManager();
        var config = new DaprSidecarConfig(
            AppId: "my-app",
            AppPort: 8080,
            DaprHttpPort: 3500,
            DaprGrpcPort: 50001,
            NetworkName: "spring-net-abc");

        var containerConfig = manager.BuildSidecarContainerConfig(config, "spring-dapr-net");

        containerConfig.NetworkName.ShouldBe("spring-net-abc");
    }

    [Fact]
    public void BuildSidecarContainerConfig_WithComponentsPath_AddsMountAndCommandFlag()
    {
        var manager = CreateManager();
        var config = new DaprSidecarConfig(
            AppId: "my-app",
            AppPort: 8080,
            DaprHttpPort: 3500,
            DaprGrpcPort: 50001,
            ComponentsPath: "/home/user/dapr/components");

        var containerConfig = manager.BuildSidecarContainerConfig(config, "spring-dapr-comp");

        containerConfig.VolumeMounts.ShouldNotBeNull();
        containerConfig.VolumeMounts!.ShouldContain("/home/user/dapr/components:/components");
        containerConfig.Command.ShouldNotBeNull();
        containerConfig.Command!.ShouldContain("--resources-path");
        containerConfig.Command!.ShouldContain("/components");
        // Tokens must be adjacent — "--resources-path" must immediately
        // precede "/components" so the dispatcher forwards them as a
        // single flag/value pair.
        var flagIdx = containerConfig.Command.ToList().IndexOf("--resources-path");
        containerConfig.Command[flagIdx + 1].ShouldBe("/components");
    }

    [Fact]
    public void BuildSidecarContainerConfig_HonorsImageOverride()
    {
        var manager = CreateManager(options: new DaprSidecarOptions { Image = "daprio/daprd:1.14.4" });
        var config = new DaprSidecarConfig(
            AppId: "my-app",
            AppPort: 8080,
            DaprHttpPort: 3500,
            DaprGrpcPort: 50001);

        var containerConfig = manager.BuildSidecarContainerConfig(config, "spring-dapr-pinned");

        containerConfig.Image.ShouldBe("daprio/daprd:1.14.4");
    }

    [Fact]
    public async Task StartSidecarAsync_DelegatesToRuntimeStartAsync()
    {
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("dispatcher-assigned-id"));

        var manager = CreateManager(runtime);
        var config = new DaprSidecarConfig(
            AppId: "my-app",
            AppPort: 8080,
            DaprHttpPort: 3500,
            DaprGrpcPort: 50001,
            NetworkName: "n");

        var info = await manager.StartSidecarAsync(config, TestContext.Current.CancellationToken);

        await runtime.Received(1).StartAsync(
            Arg.Is<ContainerConfig>(c => c.Image == "daprio/daprd:1.17.4" && c.NetworkName == "n"),
            Arg.Any<CancellationToken>());

        // SidecarId is the dispatcher-assigned container name (the runtime
        // overrides --name); WaitForHealthyAsync's transient probe relies
        // on this id resolving via the bridge DNS, which only happens for
        // the dispatcher-assigned name, not the human-readable label.
        info.SidecarId.ShouldBe("dispatcher-assigned-id");
        info.DaprHttpPort.ShouldBe(3500);
    }

    [Fact]
    public async Task StopSidecarAsync_DelegatesToRuntimeStopAsync()
    {
        var runtime = Substitute.For<IContainerRuntime>();
        var manager = CreateManager(runtime);

        await manager.StopSidecarAsync("sidecar-1", TestContext.Current.CancellationToken);

        await runtime.Received(1).StopAsync("sidecar-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopSidecarAsync_SwallowsRuntimeFailures()
    {
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));
        var manager = CreateManager(runtime);

        // Best-effort teardown contract — exceptions log a warning and the
        // call returns normally so the lifecycle manager's sweep continues.
        await Should.NotThrowAsync(() =>
            manager.StopSidecarAsync("sidecar-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitForHealthyAsync_ReturnsTrueOnFirstHealthyProbe()
    {
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.ProbeHttpFromTransientContainerAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Is<string>(u => u.Contains("/v1.0/healthz/outbound")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var manager = CreateManager(runtime);

        var healthy = await manager.WaitForHealthyAsync(
            new DaprSidecarInfo("sidecar-1", 3500, 50001, "spring-net-test"),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        healthy.ShouldBeTrue();
        // Probe target is the sidecar's own DNS name on the bridge (not
        // localhost), and the URL hits /healthz/outbound so daprd reports
        // ready before the paired app container is up — the lifecycle only
        // starts the app after this returns.
        await runtime.Received().ProbeHttpFromTransientContainerAsync(
            "docker.io/curlimages/curl:latest",
            "spring-net-test",
            "http://sidecar-1:3500/v1.0/healthz/outbound",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WaitForHealthyAsync_ReturnsFalseOnTimeout()
    {
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.ProbeHttpFromTransientContainerAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var manager = CreateManager(runtime, new DaprSidecarOptions
        {
            // Drive the loop fast so the test completes well under the
            // xUnit per-test timeout. The polling-interval knob is the
            // whole reason we lifted these values into options.
            HealthPollInterval = TimeSpan.FromMilliseconds(5),
        });

        var healthy = await manager.WaitForHealthyAsync(
            new DaprSidecarInfo("sidecar-1", 3500, 50001, "spring-net-test"),
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);

        healthy.ShouldBeFalse();
    }

    [Fact]
    public async Task StartSidecarAsync_RejectsConfigWithoutNetworkName()
    {
        var manager = CreateManager();
        var config = new DaprSidecarConfig(
            AppId: "no-net",
            AppPort: 8080,
            DaprHttpPort: 3500,
            DaprGrpcPort: 50001);

        // Probe path needs DNS via a bridge network; surfacing the missing
        // network at start-time keeps the failure mode obvious instead of
        // burning the full health timeout on a doomed loopback probe.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            manager.StartSidecarAsync(config, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartSidecarAsync_PopulatesNetworkNameOnReturnedInfo()
    {
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("dispatcher-id"));

        var manager = CreateManager(runtime);
        var config = new DaprSidecarConfig(
            AppId: "with-net",
            AppPort: 8080,
            DaprHttpPort: 3500,
            DaprGrpcPort: 50001,
            NetworkName: "spring-net-xyz");

        var info = await manager.StartSidecarAsync(config, TestContext.Current.CancellationToken);

        info.NetworkName.ShouldBe("spring-net-xyz");
    }
}