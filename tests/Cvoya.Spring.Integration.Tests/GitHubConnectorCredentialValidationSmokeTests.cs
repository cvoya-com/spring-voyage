// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.Connector.GitHub.DependencyInjection;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Phase 2.11 smoke test — when a host registers the GitHub connector via
/// <see cref="ServiceCollectionExtensions.AddCvoyaSpringConnectorGitHub"/>,
/// the connector is resolvable through the platform-generic
/// <see cref="IConnectorType"/> contract AND its
/// <see cref="IConnectorType.ValidateCredentialAsync(string, CancellationToken)"/>
/// override is the one that gets invoked. This is what guarantees the
/// credential-health pipeline (landing in a follow-up phase) can call the
/// hook without GitHub-specific imports.
/// </summary>
public class GitHubConnectorCredentialValidationSmokeTests
{
    [Fact]
    public async Task GitHubConnector_RegisteredViaDi_ExposesCredentialValidationHookViaIConnectorType()
    {
        // Build a minimal host with the GitHub connector registered. Leave
        // GitHub:AppId / GitHub:PrivateKeyPem unset so the credential
        // requirement reports Disabled — this lets the smoke test exercise
        // the hook end-to-end without making a real GitHub API call.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Pre-register the cross-package storage seams the connector type
        // depends on. In a real host these come from Cvoya.Spring.Dapr;
        // for the smoke test they're inert substitutes since the validation
        // hook never touches them — it only consults the configuration
        // requirement and the installations client.
        services.AddSingleton(Substitute.For<IUnitConnectorConfigStore>());
        services.AddSingleton(Substitute.For<IUnitConnectorRuntimeStore>());
        services.AddSingleton(Substitute.For<IGitHubWebhookRegistrar>());

        services.AddCvoyaSpringConnectorGitHub(configuration);

        await using var provider = services.BuildServiceProvider();

        // The GitHub connector must surface as a registered IConnectorType
        // — the install / credential-health flow consumes the abstract
        // contract, not the concrete GitHubConnectorType.
        var connectorTypes = provider.GetServices<IConnectorType>().ToList();
        var github = connectorTypes.SingleOrDefault(c => c.Slug == "github");
        github.ShouldNotBeNull("GitHub connector must register itself as IConnectorType");

        // ValidateCredentialAsync is reachable polymorphically and returns
        // a non-null result with the disabled-reason narration — proving
        // both that the override fires (the default would return null) and
        // that the connector reports its own configuration state to the
        // credential-health caller.
        var result = await github!.ValidateCredentialAsync(
            credential: string.Empty,
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull(
            "GitHub connector overrides the optional ValidateCredentialAsync hook; null would indicate the default no-op shadowed the override.");
        result!.Status.ShouldBe(CredentialValidationStatus.Unknown,
            "with no GitHub App credentials configured the hook should report Unknown rather than Invalid");
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("GitHub App not configured");

        // The container-baseline hook is also wired through the same
        // abstract contract and reports Passed — the GitHub connector has
        // no host-side binary to verify.
        var baseline = await github.VerifyContainerBaselineAsync(
            TestContext.Current.CancellationToken);

        baseline.ShouldNotBeNull(
            "GitHub connector overrides the optional VerifyContainerBaselineAsync hook; null would indicate the default no-op shadowed the override.");
        baseline!.Passed.ShouldBeTrue();
        baseline.Errors.ShouldBeEmpty();
    }
}