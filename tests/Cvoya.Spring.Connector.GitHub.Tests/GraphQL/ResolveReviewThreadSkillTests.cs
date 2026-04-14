// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.GraphQL;

using Cvoya.Spring.Connector.GitHub.GraphQL;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

public class ResolveReviewThreadSkillTests
{
    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsResolved()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var response = new ResolveReviewThreadResponse(
            new ResolveReviewThreadPayload(
                new ReviewThreadState("thread_1", IsResolved: true)));
        graphql
            .MutateAsync<ResolveReviewThreadResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var result = await new ResolveReviewThreadSkill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("thread_1", TestContext.Current.CancellationToken);

        result.GetProperty("thread_id").GetString().ShouldBe("thread_1");
        result.GetProperty("is_resolved").GetBoolean().ShouldBeTrue();
        result.GetProperty("no_op").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyResolvedError_IsTreatedAsNoOp()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        graphql
            .MutateAsync<ResolveReviewThreadResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ResolveReviewThreadResponse>>(_ =>
                throw new GitHubGraphQLException(["Thread is already resolved."]));

        var result = await new ResolveReviewThreadSkill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("thread_1", TestContext.Current.CancellationToken);

        result.GetProperty("thread_id").GetString().ShouldBe("thread_1");
        result.GetProperty("is_resolved").GetBoolean().ShouldBeTrue();
        result.GetProperty("no_op").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_OtherError_Propagates()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        graphql
            .MutateAsync<ResolveReviewThreadResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ResolveReviewThreadResponse>>(_ =>
                throw new GitHubGraphQLException(["resource not accessible by integration"]));

        await Should.ThrowAsync<GitHubGraphQLException>(() =>
            new ResolveReviewThreadSkill(graphql, NullLoggerFactory.Instance)
                .ExecuteAsync("thread_1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteAsync_MissingThreadId_Throws()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        await Should.ThrowAsync<ArgumentException>(() =>
            new ResolveReviewThreadSkill(graphql, NullLoggerFactory.Instance)
                .ExecuteAsync("   ", TestContext.Current.CancellationToken));
    }
}

public class UnresolveReviewThreadSkillTests
{
    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsUnresolved()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var response = new UnresolveReviewThreadResponse(
            new ResolveReviewThreadPayload(
                new ReviewThreadState("thread_1", IsResolved: false)));
        graphql
            .MutateAsync<UnresolveReviewThreadResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var result = await new UnresolveReviewThreadSkill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("thread_1", TestContext.Current.CancellationToken);

        result.GetProperty("thread_id").GetString().ShouldBe("thread_1");
        result.GetProperty("is_resolved").GetBoolean().ShouldBeFalse();
        result.GetProperty("no_op").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyUnresolvedError_IsTreatedAsNoOp()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        graphql
            .MutateAsync<UnresolveReviewThreadResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns<Task<UnresolveReviewThreadResponse>>(_ =>
                throw new GitHubGraphQLException(["Thread is not resolved."]));

        var result = await new UnresolveReviewThreadSkill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("thread_1", TestContext.Current.CancellationToken);

        result.GetProperty("no_op").GetBoolean().ShouldBeTrue();
        result.GetProperty("is_resolved").GetBoolean().ShouldBeFalse();
    }
}