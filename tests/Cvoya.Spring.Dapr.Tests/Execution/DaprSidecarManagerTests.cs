// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using FluentAssertions;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DaprSidecarManager"/> argument building.
/// These tests verify the sidecar run command construction without launching actual containers.
/// </summary>
public class DaprSidecarManagerTests
{
    [Fact]
    public void BuildSidecarRunArguments_MinimalConfig_ProducesCorrectCommand()
    {
        var config = new DaprSidecarConfig(
            AppId: "my-app",
            AppPort: 8080,
            DaprHttpPort: 3500,
            DaprGrpcPort: 50001);
        var sidecarName = "spring-dapr-test";

        var args = DaprSidecarManager.BuildSidecarRunArguments(config, sidecarName);

        args.Should().StartWith("run -d --name spring-dapr-test");
        args.Should().Contain("--label spring.managed=true");
        args.Should().Contain("--label spring.role=dapr-sidecar");
        args.Should().Contain("--label spring.app-id=my-app");
        args.Should().Contain("daprio/daprd:latest");
        args.Should().Contain("--app-id my-app");
        args.Should().Contain("--app-port 8080");
        args.Should().Contain("--dapr-http-port 3500");
        args.Should().Contain("--dapr-grpc-port 50001");
        args.Should().NotContain("--network");
        args.Should().NotContain("--resources-path");
    }

    [Fact]
    public void BuildSidecarRunArguments_WithNetwork_IncludesNetworkFlag()
    {
        var config = new DaprSidecarConfig(
            AppId: "my-app",
            AppPort: 8080,
            DaprHttpPort: 3500,
            DaprGrpcPort: 50001,
            NetworkName: "spring-net-abc");
        var sidecarName = "spring-dapr-net";

        var args = DaprSidecarManager.BuildSidecarRunArguments(config, sidecarName);

        args.Should().Contain("--network spring-net-abc");
    }

    [Fact]
    public void BuildSidecarRunArguments_WithComponentsPath_MountsAndConfigures()
    {
        var config = new DaprSidecarConfig(
            AppId: "my-app",
            AppPort: 8080,
            DaprHttpPort: 3500,
            DaprGrpcPort: 50001,
            ComponentsPath: "/home/user/dapr/components");
        var sidecarName = "spring-dapr-comp";

        var args = DaprSidecarManager.BuildSidecarRunArguments(config, sidecarName);

        args.Should().Contain("-v /home/user/dapr/components:/components");
        args.Should().Contain("--resources-path /components");
    }

    [Fact]
    public void BuildSidecarRunArguments_FullConfig_ProducesCorrectOrder()
    {
        var config = new DaprSidecarConfig(
            AppId: "workflow-1",
            AppPort: 9090,
            DaprHttpPort: 3501,
            DaprGrpcPort: 50002,
            ComponentsPath: "/components",
            NetworkName: "my-network");
        var sidecarName = "spring-dapr-full";

        var args = DaprSidecarManager.BuildSidecarRunArguments(config, sidecarName);

        // Image should come after flags, daprd command after image
        var imageIndex = args.IndexOf("daprio/daprd:latest", StringComparison.Ordinal);
        var networkIndex = args.IndexOf("--network my-network", StringComparison.Ordinal);
        var appIdIndex = args.IndexOf("--app-id workflow-1", StringComparison.Ordinal);

        networkIndex.Should().BeLessThan(imageIndex, "network flag should come before image");
        appIdIndex.Should().BeGreaterThan(imageIndex, "daprd args should come after image");
    }
}