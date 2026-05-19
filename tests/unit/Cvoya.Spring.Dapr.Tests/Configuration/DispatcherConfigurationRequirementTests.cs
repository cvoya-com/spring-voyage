// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Configuration;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

public class DispatcherConfigurationRequirementTests
{
    private static IOptions<DispatcherClientOptions> Opts(string? baseUrl = null, string? bearerToken = null) =>
        Options.Create(new DispatcherClientOptions { BaseUrl = baseUrl, BearerToken = bearerToken });

    private static DispatcherConfigurationRequirementOptions Mandatory(bool isMandatory = true) =>
        new(IsMandatory: isMandatory);

    [Fact]
    public async Task ValidateAsync_NonMandatory_MissingBaseUrl_ReturnsDisabledInformation()
    {
        var requirement = new DispatcherConfigurationRequirement(Opts(baseUrl: null), Mandatory(isMandatory: false));

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Disabled);
        // Optional + Disabled is informational, not a warning (issue #1747).
        status.Severity.ShouldBe(SeverityLevel.Information);
        status.Reason.ShouldNotBeNull();
        // Reason must not claim "will fail at first call" — the API host
        // never drives delegated execution (issue #1747).
        status.Reason!.ShouldNotContain("will fail at first call");
        status.Suggestion.ShouldNotBeNull();
        status.Suggestion!.ShouldContain("Dispatcher__BaseUrl");
    }

    [Fact]
    public async Task ValidateAsync_Mandatory_MissingBaseUrl_ReturnsInvalidWithFatalError()
    {
        // #2518: the API host runs PersistentAgentRegistry as a hosted
        // service and that service hits the dispatcher on the restart
        // path. A missing Dispatcher:BaseUrl must abort startup so
        // operators see a clear configuration error instead of a runtime
        // crash that DELETEs persistent_agent_runtime rows.
        var requirement = new DispatcherConfigurationRequirement(Opts(baseUrl: null), Mandatory(isMandatory: true));

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.Severity.ShouldBe(SeverityLevel.Error);
        status.FatalError.ShouldNotBeNull();
        status.Reason.ShouldNotBeNull();
        status.Reason!.ShouldContain("PersistentAgentRegistry");
        status.Suggestion.ShouldNotBeNull();
        status.Suggestion!.ShouldContain("Dispatcher__BaseUrl");
    }

    [Fact]
    public async Task ValidateAsync_MalformedBaseUrl_ReturnsInvalid()
    {
        var requirement = new DispatcherConfigurationRequirement(Opts(baseUrl: "this-is-not-a-url"), Mandatory());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.FatalError.ShouldNotBeNull();
        status.Reason!.ShouldContain("this-is-not-a-url");
    }

    [Fact]
    public async Task ValidateAsync_NonHttpScheme_ReturnsInvalid()
    {
        var requirement = new DispatcherConfigurationRequirement(
            Opts(baseUrl: "ftp://spring-dispatcher:8080/"),
            Mandatory());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
    }

    [Fact]
    public async Task ValidateAsync_BaseUrlWithoutBearer_ReturnsMetWithWarning()
    {
        var requirement = new DispatcherConfigurationRequirement(
            Opts(baseUrl: "http://spring-dispatcher:8080/"),
            Mandatory());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
        status.Severity.ShouldBe(SeverityLevel.Warning);
        status.Reason!.ShouldContain("BearerToken");
    }

    [Fact]
    public async Task ValidateAsync_ValidBaseUrlAndBearer_ReturnsMet()
    {
        var requirement = new DispatcherConfigurationRequirement(
            Opts(baseUrl: "https://spring-dispatcher.example.com/", bearerToken: "s3cr3t"),
            Mandatory());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
        status.Severity.ShouldBe(SeverityLevel.Information);
    }

    [Fact]
    public async Task RequirementMetadata_Mandatory_IsStable()
    {
        var requirement = new DispatcherConfigurationRequirement(Opts(), Mandatory(isMandatory: true));

        requirement.RequirementId.ShouldBe("dispatcher-endpoint");
        requirement.SubsystemName.ShouldBe("Dispatcher");
        requirement.IsMandatory.ShouldBeTrue();
        requirement.EnvironmentVariableNames.ShouldContain("Dispatcher__BaseUrl");
        requirement.EnvironmentVariableNames.ShouldContain("Dispatcher__BearerToken");
        requirement.ConfigurationSectionPath.ShouldBe(DispatcherClientOptions.SectionName);
        requirement.DocumentationUrl.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RequirementMetadata_NonMandatory_ReflectsRegistrationOptions()
    {
        var requirement = new DispatcherConfigurationRequirement(Opts(), Mandatory(isMandatory: false));

        requirement.IsMandatory.ShouldBeFalse();
        await Task.CompletedTask;
    }
}
