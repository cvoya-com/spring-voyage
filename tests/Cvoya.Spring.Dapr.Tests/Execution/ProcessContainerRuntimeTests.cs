// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using FluentAssertions;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ProcessContainerRuntime"/> command building.
/// These tests verify the argument construction without launching actual containers.
/// </summary>
public class ProcessContainerRuntimeTests
{
    [Fact]
    public void BuildRunArguments_MinimalConfig_ProducesCorrectCommand()
    {
        var config = new ContainerConfig(Image: "my-image:latest");
        var containerName = "spring-exec-test";

        var args = ProcessContainerRuntime.BuildRunArguments(config, containerName);

        args.Should().Be("run --rm --name spring-exec-test my-image:latest");
    }

    [Fact]
    public void BuildRunArguments_WithEnvironmentVariables_IncludesEnvFlags()
    {
        var config = new ContainerConfig(
            Image: "my-image:latest",
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["SPRING_SYSTEM_PROMPT"] = "hello",
                ["OTHER_VAR"] = "world"
            });
        var containerName = "spring-exec-env";

        var args = ProcessContainerRuntime.BuildRunArguments(config, containerName);

        args.Should().Contain("-e SPRING_SYSTEM_PROMPT=hello");
        args.Should().Contain("-e OTHER_VAR=world");
    }

    [Fact]
    public void BuildRunArguments_WithVolumeMounts_IncludesVolumeFlags()
    {
        var config = new ContainerConfig(
            Image: "my-image:latest",
            VolumeMounts: ["/host/path:/container/path", "/data:/data:ro"]);
        var containerName = "spring-exec-vol";

        var args = ProcessContainerRuntime.BuildRunArguments(config, containerName);

        args.Should().Contain("-v /host/path:/container/path");
        args.Should().Contain("-v /data:/data:ro");
    }

    [Fact]
    public void BuildRunArguments_WithCommand_AppendsCommand()
    {
        var config = new ContainerConfig(
            Image: "my-image:latest",
            Command: "bash -c 'echo hello'");
        var containerName = "spring-exec-cmd";

        var args = ProcessContainerRuntime.BuildRunArguments(config, containerName);

        args.Should().EndWith("my-image:latest bash -c 'echo hello'");
    }

    [Fact]
    public void BuildRunArguments_FullConfig_ProducesCorrectOrder()
    {
        var config = new ContainerConfig(
            Image: "agent:v1",
            Command: "run-agent",
            EnvironmentVariables: new Dictionary<string, string> { ["KEY"] = "val" },
            VolumeMounts: ["/src:/app"]);
        var containerName = "spring-exec-full";

        var args = ProcessContainerRuntime.BuildRunArguments(config, containerName);

        // Env and volume flags should come before the image.
        var imageIndex = args.IndexOf("agent:v1", StringComparison.Ordinal);
        var envIndex = args.IndexOf("-e KEY=val", StringComparison.Ordinal);
        var volIndex = args.IndexOf("-v /src:/app", StringComparison.Ordinal);
        var cmdIndex = args.IndexOf("run-agent", StringComparison.Ordinal);

        envIndex.Should().BeLessThan(imageIndex);
        volIndex.Should().BeLessThan(imageIndex);
        imageIndex.Should().BeLessThan(cmdIndex);
    }

    [Fact]
    public void BuildRunArguments_WithNetworkName_IncludesNetworkFlag()
    {
        var config = new ContainerConfig(
            Image: "my-image:latest",
            NetworkName: "spring-net-abc");
        var containerName = "spring-exec-net";

        var args = ProcessContainerRuntime.BuildRunArguments(config, containerName);

        args.Should().Contain("--network spring-net-abc");
    }

    [Fact]
    public void BuildRunArguments_WithLabels_IncludesLabelFlags()
    {
        var config = new ContainerConfig(
            Image: "my-image:latest",
            Labels: new Dictionary<string, string>
            {
                ["spring.managed"] = "true",
                ["spring.role"] = "workflow"
            });
        var containerName = "spring-exec-labels";

        var args = ProcessContainerRuntime.BuildRunArguments(config, containerName);

        args.Should().Contain("--label spring.managed=true");
        args.Should().Contain("--label spring.role=workflow");
    }

    [Fact]
    public void BuildRunArguments_NetworkAndLabels_ComeBeforeEnvAndVolume()
    {
        var config = new ContainerConfig(
            Image: "agent:v1",
            NetworkName: "my-net",
            Labels: new Dictionary<string, string> { ["app"] = "test" },
            EnvironmentVariables: new Dictionary<string, string> { ["KEY"] = "val" },
            VolumeMounts: ["/src:/app"]);
        var containerName = "spring-exec-order";

        var args = ProcessContainerRuntime.BuildRunArguments(config, containerName);

        var networkIndex = args.IndexOf("--network", StringComparison.Ordinal);
        var labelIndex = args.IndexOf("--label", StringComparison.Ordinal);
        var envIndex = args.IndexOf("-e KEY", StringComparison.Ordinal);
        var imageIndex = args.IndexOf("agent:v1", StringComparison.Ordinal);

        networkIndex.Should().BeLessThan(labelIndex);
        labelIndex.Should().BeLessThan(envIndex);
        envIndex.Should().BeLessThan(imageIndex);
    }
}