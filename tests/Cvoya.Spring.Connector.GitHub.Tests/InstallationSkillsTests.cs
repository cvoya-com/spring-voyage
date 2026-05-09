// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

public class InstallationSkillsTests
{
    private readonly IGitHubInstallationsClient _installations;
    private readonly ILoggerFactory _loggerFactory;

    public InstallationSkillsTests()
    {
        _installations = Substitute.For<IGitHubInstallationsClient>();
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    [Fact]
    public async Task ListInstallations_ReturnsStructuredList()
    {
        _installations.ListInstallationsAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new GitHubInstallation(100L, "acme", "Organization", "all"),
            new GitHubInstallation(200L, "octocat", "User", "selected"),
        });

        var result = await new ListInstallationsSkill(_installations, _loggerFactory)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        result.GetProperty("count").GetInt32().ShouldBe(2);
        var first = result.GetProperty("installations")[0];
        first.GetProperty("id").GetInt64().ShouldBe(100L);
        first.GetProperty("account").GetString().ShouldBe("acme");
        first.GetProperty("account_type").GetString().ShouldBe("Organization");
        first.GetProperty("repo_selection").GetString().ShouldBe("all");
    }

    [Fact]
    public async Task ListInstallationRepositories_ReturnsStructuredList()
    {
        _installations.ListInstallationRepositoriesAsync(100L, Arg.Any<CancellationToken>()).Returns(new[]
        {
            new GitHubInstallationRepository(1L, "acme", "repo-a", "acme/repo-a", false),
            new GitHubInstallationRepository(2L, "acme", "repo-b", "acme/repo-b", true),
        });

        var result = await new ListInstallationRepositoriesSkill(_installations, _loggerFactory)
            .ExecuteAsync(100L, TestContext.Current.CancellationToken);

        result.GetProperty("installation_id").GetInt64().ShouldBe(100L);
        result.GetProperty("count").GetInt32().ShouldBe(2);
        result.GetProperty("repositories")[0].GetProperty("full_name").GetString().ShouldBe("acme/repo-a");
        result.GetProperty("repositories")[1].GetProperty("private").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task FindInstallationForRepo_Installed_ReturnsInstallationMetadata()
    {
        _installations.FindInstallationForRepoAsync("acme", "repo-a", Arg.Any<CancellationToken>())
            .Returns(new GitHubInstallation(100L, "acme", "Organization", "all"));

        var result = await new FindInstallationForRepoSkill(_installations, _loggerFactory)
            .ExecuteAsync("acme", "repo-a", TestContext.Current.CancellationToken);

        result.GetProperty("installed").GetBoolean().ShouldBeTrue();
        result.GetProperty("installation_id").GetInt64().ShouldBe(100L);
        result.GetProperty("account").GetString().ShouldBe("acme");
    }

    [Fact]
    public async Task FindInstallationForRepo_NotInstalled_ReturnsInstalledFalse()
    {
        _installations.FindInstallationForRepoAsync("other", "repo-x", Arg.Any<CancellationToken>())
            .Returns((GitHubInstallation?)null);

        var result = await new FindInstallationForRepoSkill(_installations, _loggerFactory)
            .ExecuteAsync("other", "repo-x", TestContext.Current.CancellationToken);

        result.GetProperty("installed").GetBoolean().ShouldBeFalse();
        result.GetProperty("owner").GetString().ShouldBe("other");
        result.GetProperty("repo").GetString().ShouldBe("repo-x");
        result.TryGetProperty("installation_id", out _).ShouldBeFalse();
    }
}
