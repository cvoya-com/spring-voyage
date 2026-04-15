// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.GraphQL;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Caching;
using Cvoya.Spring.Connector.GitHub.GraphQL;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

public class AddProjectV2ItemSkillTests
{
    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsItemAndInvalidatesBoardTag()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var cache = Substitute.For<IGitHubResponseCache>();

        var added = new ProjectV2Item(
            Id: "PVTI_new",
            Type: "ISSUE",
            IsArchived: false,
            CreatedAt: "2026-04-13T00:00:00Z",
            UpdatedAt: "2026-04-13T00:00:00Z",
            Content: JsonSerializer.SerializeToElement(new
            {
                __typename = "Issue",
                id = "I_1",
                number = 1,
                title = "Bug",
                url = "https://github.com/acme/r/issues/1",
                state = "OPEN",
                repository = new { nameWithOwner = "acme/r" },
            }),
            FieldValues: new ProjectV2FieldValueConnection([]));

        graphql
            .MutateAsync<AddProjectV2ItemResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AddProjectV2ItemResponse(new AddProjectV2ItemPayload(added))));

        var skill = new AddProjectV2ItemSkill(graphql, cache, NullLoggerFactory.Instance);
        var result = await skill.ExecuteAsync(
            "PVT_1", "I_1", owner: "acme", number: 7,
            cancellationToken: TestContext.Current.CancellationToken);

        result.GetProperty("added").GetBoolean().ShouldBeTrue();
        result.GetProperty("project_id").GetString().ShouldBe("PVT_1");
        result.GetProperty("content_id").GetString().ShouldBe("I_1");
        result.GetProperty("item").GetProperty("item_id").GetString().ShouldBe("PVTI_new");

        await cache.Received(1).InvalidateByTagAsync(
            "project-v2:acme/7",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NoOwnerNumber_StillSucceedsButSkipsTagInvalidation()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var cache = Substitute.For<IGitHubResponseCache>();

        var added = new ProjectV2Item(
            Id: "PVTI_x", Type: null, IsArchived: false,
            CreatedAt: null, UpdatedAt: null,
            Content: null, FieldValues: null);

        graphql
            .MutateAsync<AddProjectV2ItemResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AddProjectV2ItemResponse(new AddProjectV2ItemPayload(added))));

        var skill = new AddProjectV2ItemSkill(graphql, cache, NullLoggerFactory.Instance);
        var result = await skill.ExecuteAsync("PVT_1", "I_1", cancellationToken: TestContext.Current.CancellationToken);

        result.GetProperty("added").GetBoolean().ShouldBeTrue();
        await cache.DidNotReceive().InvalidateByTagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_GraphQLError_PropagatesAndDoesNotInvalidate()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var cache = Substitute.For<IGitHubResponseCache>();

        graphql
            .MutateAsync<AddProjectV2ItemResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns<Task<AddProjectV2ItemResponse>>(_ => throw new GitHubGraphQLException(["forbidden"]));

        var skill = new AddProjectV2ItemSkill(graphql, cache, NullLoggerFactory.Instance);

        await Should.ThrowAsync<GitHubGraphQLException>(() => skill.ExecuteAsync(
            "PVT_1", "I_1", owner: "acme", number: 7,
            cancellationToken: TestContext.Current.CancellationToken));

        await cache.DidNotReceive().InvalidateByTagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NullItemPayload_ReturnsAddedFalse()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var cache = Substitute.For<IGitHubResponseCache>();

        graphql
            .MutateAsync<AddProjectV2ItemResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AddProjectV2ItemResponse(new AddProjectV2ItemPayload(null))));

        var skill = new AddProjectV2ItemSkill(graphql, cache, NullLoggerFactory.Instance);
        var result = await skill.ExecuteAsync("PVT_1", "I_1", owner: "acme", number: 7,
            cancellationToken: TestContext.Current.CancellationToken);

        result.GetProperty("added").GetBoolean().ShouldBeFalse();
        await cache.DidNotReceive().InvalidateByTagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}