// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.GraphQL;

using Cvoya.Spring.Connector.GitHub.Caching;
using Cvoya.Spring.Connector.GitHub.GraphQL;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

public class DeleteProjectV2ItemSkillTests
{
    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsDeletedIdAndInvalidates()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var cache = Substitute.For<IGitHubResponseCache>();

        graphql
            .MutateAsync<DeleteProjectV2ItemResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeleteProjectV2ItemResponse(new DeleteProjectV2ItemPayload("PVTI_1"))));

        var skill = new DeleteProjectV2ItemSkill(graphql, cache, NullLoggerFactory.Instance);
        var result = await skill.ExecuteAsync("PVT_1", "PVTI_1", owner: "acme", number: 7,
            cancellationToken: TestContext.Current.CancellationToken);

        result.GetProperty("deleted").GetBoolean().ShouldBeTrue();
        result.GetProperty("deleted_id").GetString().ShouldBe("PVTI_1");

        await cache.Received(1).InvalidateByTagAsync("project-v2-item:PVTI_1", Arg.Any<CancellationToken>());
        await cache.Received(1).InvalidateByTagAsync("project-v2:acme/7", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NullDeletedId_ReturnsDeletedFalse()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var cache = Substitute.For<IGitHubResponseCache>();

        graphql
            .MutateAsync<DeleteProjectV2ItemResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeleteProjectV2ItemResponse(new DeleteProjectV2ItemPayload(null))));

        var skill = new DeleteProjectV2ItemSkill(graphql, cache, NullLoggerFactory.Instance);
        var result = await skill.ExecuteAsync("PVT_1", "PVTI_1", cancellationToken: TestContext.Current.CancellationToken);

        result.GetProperty("deleted").GetBoolean().ShouldBeFalse();
        // Item tag still invalidated even when payload is null — the mutation
        // succeeded from a transport standpoint; a missing deletedItemId is
        // GitHub-side weirdness, not an error. The caller sees deleted=false.
        await cache.Received(1).InvalidateByTagAsync("project-v2-item:PVTI_1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_GraphQLError_DoesNotInvalidate()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var cache = Substitute.For<IGitHubResponseCache>();

        graphql
            .MutateAsync<DeleteProjectV2ItemResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeleteProjectV2ItemResponse>>(_ => throw new GitHubGraphQLException(["forbidden"]));

        var skill = new DeleteProjectV2ItemSkill(graphql, cache, NullLoggerFactory.Instance);

        await Should.ThrowAsync<GitHubGraphQLException>(() => skill.ExecuteAsync(
            "PVT_1", "PVTI_1", owner: "acme", number: 7,
            cancellationToken: TestContext.Current.CancellationToken));

        await cache.DidNotReceive().InvalidateByTagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}