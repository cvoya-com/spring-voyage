// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Configuration;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.State;

using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

public class DaprStateStoreConfigurationRequirementTests
{
    [Fact]
    public async Task ValidateAsync_DefaultStoreName_ReturnsMet()
    {
        var options = Options.Create(new DaprStateStoreOptions());
        var requirement = new DaprStateStoreConfigurationRequirement(options);

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
    }

    [Fact]
    public async Task ValidateAsync_CustomStoreName_ReturnsMet()
    {
        var options = Options.Create(new DaprStateStoreOptions { StoreName = "custom-store" });
        var requirement = new DaprStateStoreConfigurationRequirement(options);

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
    }

    [Fact]
    public async Task ValidateAsync_EmptyStoreName_ReturnsInvalidWithFatal()
    {
        var options = Options.Create(new DaprStateStoreOptions { StoreName = "" });
        var requirement = new DaprStateStoreConfigurationRequirement(options);

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.FatalError.ShouldNotBeNull();
        status.Reason.ShouldNotBeNull();
        status.Reason!.ShouldContain("DaprStateStore:StoreName");
    }

    [Fact]
    public async Task ValidateAsync_WhitespaceStoreName_ReturnsInvalid()
    {
        var options = Options.Create(new DaprStateStoreOptions { StoreName = "   " });
        var requirement = new DaprStateStoreConfigurationRequirement(options);

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
    }

    [Fact]
    public async Task RequirementMetadata_IsStable()
    {
        var options = Options.Create(new DaprStateStoreOptions());
        var requirement = new DaprStateStoreConfigurationRequirement(options);

        requirement.RequirementId.ShouldBe("dapr-state-store");
        requirement.SubsystemName.ShouldBe("Dapr State Store");
        requirement.IsMandatory.ShouldBeTrue();
        requirement.EnvironmentVariableNames.ShouldContain("DaprStateStore__StoreName");
        requirement.ConfigurationSectionPath.ShouldBe(DaprStateStoreOptions.SectionName);
        requirement.DocumentationUrl.ShouldNotBeNull();
        await Task.CompletedTask;
    }
}
