// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.GraphQL;

using Cvoya.Spring.Connector.GitHub.GraphQL;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

public class ListReviewThreadsSkillTests
{
    [Fact]
    public async Task ExecuteAsync_MixOfResolvedAndUnresolved_ProjectsCorrectly()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();

        var response = new ReviewThreadsResponse(
            new RepositoryWithPullRequest(
                new PullRequestWithReviewThreads(
                    new ReviewThreadConnection(
                    [
                        new ReviewThreadNode(
                            Id: "thread_1",
                            IsResolved: false,
                            IsOutdated: false,
                            Path: "src/Foo.cs",
                            Line: 42,
                            Comments: new ReviewThreadCommentConnection(
                            [
                                new ReviewThreadComment("c1", 1001, "Needs fix", new ReviewThreadAuthor("reviewer-a")),
                            ])),
                        new ReviewThreadNode(
                            Id: "thread_2",
                            IsResolved: true,
                            IsOutdated: false,
                            Path: "src/Bar.cs",
                            Line: 10,
                            Comments: new ReviewThreadCommentConnection(
                            [
                                new ReviewThreadComment("c2", 1002, "nit", new ReviewThreadAuthor("reviewer-b")),
                            ])),
                    ]))));

        graphql
            .QueryAsync<ReviewThreadsResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var skill = new ListReviewThreadsSkill(graphql, NullLoggerFactory.Instance);
        var result = await skill.ExecuteAsync("o", "r", 42, TestContext.Current.CancellationToken);

        result.GetProperty("thread_count").GetInt32().ShouldBe(2);
        result.GetProperty("unresolved_count").GetInt32().ShouldBe(1);
        result.GetProperty("has_unresolved_review_threads").GetBoolean().ShouldBeTrue();

        var threads = result.GetProperty("threads");
        threads.GetArrayLength().ShouldBe(2);
        threads[0].GetProperty("thread_id").GetString().ShouldBe("thread_1");
        threads[0].GetProperty("is_resolved").GetBoolean().ShouldBeFalse();
        threads[0].GetProperty("path").GetString().ShouldBe("src/Foo.cs");
        threads[0].GetProperty("line").GetInt32().ShouldBe(42);
        threads[0].GetProperty("comments")[0].GetProperty("author").GetString().ShouldBe("reviewer-a");
        threads[1].GetProperty("is_resolved").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NoThreads_ReturnsEmptyCollection()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var response = new ReviewThreadsResponse(
            new RepositoryWithPullRequest(
                new PullRequestWithReviewThreads(
                    new ReviewThreadConnection([]))));
        graphql
            .QueryAsync<ReviewThreadsResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var result = await new ListReviewThreadsSkill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("o", "r", 1, TestContext.Current.CancellationToken);

        result.GetProperty("thread_count").GetInt32().ShouldBe(0);
        result.GetProperty("has_unresolved_review_threads").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_MissingPullRequest_ReturnsEmptyCollection()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        graphql
            .QueryAsync<ReviewThreadsResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ReviewThreadsResponse(null)));

        var result = await new ListReviewThreadsSkill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("o", "r", 999, TestContext.Current.CancellationToken);

        result.GetProperty("thread_count").GetInt32().ShouldBe(0);
    }
}