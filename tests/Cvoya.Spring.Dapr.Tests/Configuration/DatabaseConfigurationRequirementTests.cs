// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Configuration;

using System.Collections.Generic;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Configuration;

using Microsoft.Extensions.Configuration;

using Shouldly;

using Xunit;

public class DatabaseConfigurationRequirementTests
{
    [Fact]
    public async Task ValidateAsync_PreRegisteredDbContextOptions_ReturnsMetWithWarning()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var requirement = new DatabaseConfigurationRequirement(
            configuration,
            new DatabaseConfigurationRequirement.TestHarnessSignal(PreRegistered: true));
        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
        status.Severity.ShouldBe(SeverityLevel.Warning);
        status.Reason.ShouldNotBeNull();
        status.Reason!.ShouldContain("pre-registered");
    }

    [Fact]
    public async Task ValidateAsync_MissingConnectionString_ReturnsInvalidWithFatalError()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var requirement = new DatabaseConfigurationRequirement(
            configuration,
            new DatabaseConfigurationRequirement.TestHarnessSignal(PreRegistered: false));
        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.FatalError.ShouldNotBeNull();
        status.Reason.ShouldNotBeNull();
        status.Reason!.ShouldContain("ConnectionStrings:SpringDb");
    }

    [Fact]
    public async Task ValidateAsync_ValidConnectionString_ReturnsMet()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SpringDb"] = "Host=test;Database=test;Username=test;Password=test",
            })
            .Build();

        var requirement = new DatabaseConfigurationRequirement(
            configuration,
            new DatabaseConfigurationRequirement.TestHarnessSignal(PreRegistered: false));
        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
    }

    [Fact]
    public async Task ValidateAsync_MalformedConnectionString_ReturnsInvalidWithFatalError()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SpringDb"] = "this is=definitely,not;a valid postgres",
            })
            .Build();

        var requirement = new DatabaseConfigurationRequirement(
            configuration,
            new DatabaseConfigurationRequirement.TestHarnessSignal(PreRegistered: false));
        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.FatalError.ShouldNotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_RequirementMetadata_IsStable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var requirement = new DatabaseConfigurationRequirement(
            configuration,
            new DatabaseConfigurationRequirement.TestHarnessSignal(PreRegistered: false));

        requirement.RequirementId.ShouldBe("database-connection-string");
        requirement.SubsystemName.ShouldBe("Database");
        requirement.IsMandatory.ShouldBeTrue();
        requirement.EnvironmentVariableNames.ShouldContain("ConnectionStrings__SpringDb");
        requirement.ConfigurationSectionPath.ShouldBe("ConnectionStrings:SpringDb");
        requirement.DocumentationUrl.ShouldNotBeNull();
        await Task.CompletedTask;
    }
}