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

public class ArchiveProjectV2ItemSkillTests
{
    [Fact]
    public async Task ExecuteAsync_HappyPath_InvalidatesItemAndBoardTags()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var cache = Substitute.For<IGitHubResponseCache>();

        var archived = new ProjectV2Item(
            Id: "PVTI_1", Type: "ISSUE", IsArchived: true,
            CreatedAt: null, UpdatedAt: "2026-04-13T12:00:00Z",
            Content: null, FieldValues: null);

        graphql
            .MutateAsync<ArchiveProjectV2ItemResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ArchiveProjectV2ItemResponse(new ArchiveProjectV2ItemPayload(archived))));

        var skill = new ArchiveProjectV2ItemSkill(graphql, cache, NullLoggerFactory.Instance);
        var result = await skill.ExecuteAsync("PVT_1", "PVTI_1", owner: "acme", number: 7,
            cancellationToken: TestContext.Current.CancellationToken);

        result.GetProperty("archived").GetBoolean().ShouldBeTrue();
        result.GetProperty("is_archived").GetBoolean().ShouldBeTrue();
        result.GetProperty("updated_at").GetString().ShouldBe("2026-04-13T12:00:00Z");

        await cache.Received(1).InvalidateByTagAsync("project-v2-item:PVTI_1", Arg.Any<CancellationToken>());
        await cache.Received(1).InvalidateByTagAsync("project-v2:acme/7", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithoutOwnerNumber_InvalidatesOnlyItemTag()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var cache = Substitute.For<IGitHubResponseCache>();

        graphql
            .MutateAsync<ArchiveProjectV2ItemResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ArchiveProjectV2ItemResponse(new ArchiveProjectV2ItemPayload(null))));

        var skill = new ArchiveProjectV2ItemSkill(graphql, cache, NullLoggerFactory.Instance);
        await skill.ExecuteAsync("PVT_1", "PVTI_1", cancellationToken: TestContext.Current.CancellationToken);

        await cache.Received(1).InvalidateByTagAsync("project-v2-item:PVTI_1", Arg.Any<CancellationToken>());
        await cache.DidNotReceive().InvalidateByTagAsync(Arg.Is<string>(t => t.StartsWith("project-v2:")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_GraphQLError_DoesNotInvalidate()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var cache = Substitute.For<IGitHubResponseCache>();

        graphql
            .MutateAsync<ArchiveProjectV2ItemResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ArchiveProjectV2ItemResponse>>(_ => throw new GitHubGraphQLException(["unauthorized"]));

        var skill = new ArchiveProjectV2ItemSkill(graphql, cache, NullLoggerFactory.Instance);

        await Should.ThrowAsync<GitHubGraphQLException>(() => skill.ExecuteAsync(
            "PVT_1", "PVTI_1", owner: "acme", number: 7,
            cancellationToken: TestContext.Current.CancellationToken));

        await cache.DidNotReceive().InvalidateByTagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}