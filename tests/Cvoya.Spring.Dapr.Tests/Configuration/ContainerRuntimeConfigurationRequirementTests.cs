// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Configuration;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

public class ContainerRuntimeConfigurationRequirementTests
{
    private static IOptions<ContainerRuntimeOptions> Opts(string runtime) =>
        Options.Create(new ContainerRuntimeOptions { RuntimeType = runtime });

    [Theory]
    [InlineData("podman")]
    [InlineData("docker")]
    [InlineData("Podman")]
    [InlineData("DOCKER")]
    public async Task ValidateAsync_SupportedRuntime_ReturnsMet(string runtime)
    {
        var requirement = new ContainerRuntimeConfigurationRequirement(Opts(runtime));

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
    }

    [Fact]
    public async Task ValidateAsync_DefaultOptions_ReturnsMet()
    {
        var requirement = new ContainerRuntimeConfigurationRequirement(
            Options.Create(new ContainerRuntimeOptions()));

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
    }

    [Fact]
    public async Task ValidateAsync_EmptyRuntime_ReturnsInvalid()
    {
        var requirement = new ContainerRuntimeConfigurationRequirement(Opts(""));

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.Reason.ShouldNotBeNull();
        status.Suggestion.ShouldNotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_UnknownRuntime_ReturnsInvalid()
    {
        var requirement = new ContainerRuntimeConfigurationRequirement(Opts("kubernetes"));

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.Reason!.ShouldContain("kubernetes");
        status.Suggestion!.ShouldContain("podman");
        status.Suggestion!.ShouldContain("docker");
    }

    [Fact]
    public async Task RequirementMetadata_IsStable()
    {
        var requirement = new ContainerRuntimeConfigurationRequirement(
            Options.Create(new ContainerRuntimeOptions()));

        requirement.RequirementId.ShouldBe("container-runtime-type");
        requirement.SubsystemName.ShouldBe("Container Runtime");
        requirement.IsMandatory.ShouldBeFalse();
        requirement.EnvironmentVariableNames.ShouldContain("ContainerRuntime__RuntimeType");
        requirement.ConfigurationSectionPath.ShouldBe("ContainerRuntime");
        requirement.DocumentationUrl.ShouldNotBeNull();
        await Task.CompletedTask;
    }
}
