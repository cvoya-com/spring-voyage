// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class ListPullRequestsSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly ListPullRequestsSkill _skill;

    public ListPullRequestsSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new ListPullRequestsSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_BuildsFilter_AndClampsPageSize()
    {
        PullRequestRequest? capturedFilter = null;
        ApiOptions? capturedOptions = null;
        _gitHubClient.PullRequest
            .GetAllForRepository(
                "owner", "repo",
                Arg.Do<PullRequestRequest>(f => capturedFilter = f),
                Arg.Do<ApiOptions>(o => capturedOptions = o))
            .Returns(new[]
            {
                PrTestHelpers.CreatePullRequest(1, title: "A", authorLogin: "alice"),
                PrTestHelpers.CreatePullRequest(2, title: "B", authorLogin: "bob"),
            });

        var result = await _skill.ExecuteAsync(
            "owner", "repo",
            state: "closed", head: "alice:feature", @base: "main",
            sort: "updated", direction: "asc",
            maxResults: 500,
            TestContext.Current.CancellationToken);

        capturedFilter.ShouldNotBeNull();
        capturedFilter!.State.ShouldBe(ItemStateFilter.Closed);
        capturedFilter.Head.ShouldBe("alice:feature");
        capturedFilter.Base.ShouldBe("main");
        capturedFilter.SortProperty.ShouldBe(PullRequestSort.Updated);
        capturedFilter.SortDirection.ShouldBe(Octokit.SortDirection.Ascending);

        capturedOptions.ShouldNotBeNull();
        capturedOptions!.PageSize.ShouldBe(100);

        result.GetProperty("count").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsStateOpenAndSortCreatedDesc()
    {
        PullRequestRequest? capturedFilter = null;
        _gitHubClient.PullRequest
            .GetAllForRepository(
                "owner", "repo",
                Arg.Do<PullRequestRequest>(f => capturedFilter = f),
                Arg.Any<ApiOptions>())
            .Returns(Array.Empty<PullRequest>());

        await _skill.ExecuteAsync(
            "owner", "repo",
            state: null, head: null, @base: null, sort: null, direction: null,
            maxResults: 30,
            TestContext.Current.CancellationToken);

        capturedFilter.ShouldNotBeNull();
        capturedFilter!.State.ShouldBe(ItemStateFilter.Open);
        capturedFilter.SortProperty.ShouldBe(PullRequestSort.Created);
        capturedFilter.SortDirection.ShouldBe(Octokit.SortDirection.Descending);
    }
}
