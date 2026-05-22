// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Configuration;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

public class CallbackBaseUrlConfigurationRequirementTests
{
    private static IOptions<CallbackBaseUrlOptions> Opts(string? baseUrl = null) =>
        Options.Create(new CallbackBaseUrlOptions { BaseUrl = baseUrl });

    private static CallbackBaseUrlConfigurationRequirementOptions Registration(bool isMandatory = true) =>
        new(IsMandatory: isMandatory);

    [Fact]
    public async Task ValidateAsync_NonMandatory_MissingBaseUrl_ReturnsDisabledInformation()
    {
        var requirement = new CallbackBaseUrlConfigurationRequirement(
            Opts(baseUrl: null), Registration(isMandatory: false));

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Disabled);
        status.Severity.ShouldBe(SeverityLevel.Information);
        status.Reason.ShouldNotBeNull();
        status.Suggestion.ShouldNotBeNull();
        status.Suggestion!.ShouldContain("CallbackBaseUrl__BaseUrl");
        status.FatalError.ShouldBeNull();
    }

    [Fact]
    public async Task ValidateAsync_Mandatory_MissingBaseUrl_ReturnsInvalidWithFatalError()
    {
        var requirement = new CallbackBaseUrlConfigurationRequirement(
            Opts(baseUrl: null), Registration(isMandatory: true));

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.Severity.ShouldBe(SeverityLevel.Error);
        status.FatalError.ShouldNotBeNull();
        status.Reason.ShouldNotBeNull();
        status.Reason!.ShouldContain("SPRING_CALLBACK_URL");
        status.Suggestion.ShouldNotBeNull();
        status.Suggestion!.ShouldContain("CallbackBaseUrl__BaseUrl");
    }

    [Fact]
    public async Task ValidateAsync_Mandatory_WhitespaceBaseUrl_ReturnsInvalidWithFatalError()
    {
        var requirement = new CallbackBaseUrlConfigurationRequirement(
            Opts(baseUrl: "   "), Registration(isMandatory: true));

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.FatalError.ShouldNotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_MalformedBaseUrl_ReturnsInvalid()
    {
        var requirement = new CallbackBaseUrlConfigurationRequirement(
            Opts(baseUrl: "this-is-not-a-url"), Registration());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.FatalError.ShouldNotBeNull();
        status.Reason!.ShouldContain("this-is-not-a-url");
    }

    [Fact]
    public async Task ValidateAsync_NonHttpScheme_ReturnsInvalid()
    {
        var requirement = new CallbackBaseUrlConfigurationRequirement(
            Opts(baseUrl: "ftp://spring-caddy:8443/"), Registration());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.FatalError.ShouldNotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_ValidHttpBaseUrl_ReturnsMet()
    {
        var requirement = new CallbackBaseUrlConfigurationRequirement(
            Opts(baseUrl: "http://spring-caddy:8443/"), Registration());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
        status.Severity.ShouldBe(SeverityLevel.Information);
    }

    [Fact]
    public async Task ValidateAsync_ValidHttpsBaseUrl_ReturnsMet()
    {
        var requirement = new CallbackBaseUrlConfigurationRequirement(
            Opts(baseUrl: "https://api.example.com/"), Registration());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
    }

    [Fact]
    public async Task RequirementMetadata_Mandatory_IsStable()
    {
        var requirement = new CallbackBaseUrlConfigurationRequirement(
            Opts(), Registration(isMandatory: true));

        requirement.RequirementId.ShouldBe("callback-base-url");
        requirement.SubsystemName.ShouldBe("Callback base URL");
        requirement.IsMandatory.ShouldBeTrue();
        requirement.EnvironmentVariableNames.ShouldContain("CallbackBaseUrl__BaseUrl");
        requirement.ConfigurationSectionPath.ShouldBe(CallbackBaseUrlOptions.SectionName + ":BaseUrl");
        requirement.DocumentationUrl.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RequirementMetadata_NonMandatory_ReflectsRegistrationOptions()
    {
        var requirement = new CallbackBaseUrlConfigurationRequirement(
            Opts(), Registration(isMandatory: false));

        requirement.IsMandatory.ShouldBeFalse();
        await Task.CompletedTask;
    }
}
