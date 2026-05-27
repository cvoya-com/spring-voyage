// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub;
using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Configuration;
using Cvoya.Spring.Connectors;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the GitHub connector at <see cref="BindingScope.Unit"/>. The
/// platform's tenant-scoped binding surface (ADR-0061 §1) is reserved
/// for workspace-shaped connectors (Slack, calendar, ...); GitHub stays
/// per-unit.
/// </summary>
public class GitHubConnectorTypeBindingScopeTests
{
    [Fact]
    public void BindingScope_IsUnit()
    {
        var sut = CreateSut();
        sut.BindingScope.ShouldBe(BindingScope.Unit);
    }

    private static GitHubConnectorType CreateSut()
    {
        var options = Options.Create(new GitHubConnectorOptions());
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>())
            .Returns(Substitute.For<ILogger>());

        var sp = new ServiceCollection().BuildServiceProvider();

        return new GitHubConnectorType(
            Substitute.For<IUnitConnectorConfigStore>(),
            Substitute.For<IGitHubInstallationsClient>(),
            Substitute.For<IGitHubCollaboratorsClient>(),
            options,
            new GitHubAppConfigurationRequirement(options),
            Substitute.For<IOAuthSessionStore>(),
            sp,
            loggerFactory);
    }
}
