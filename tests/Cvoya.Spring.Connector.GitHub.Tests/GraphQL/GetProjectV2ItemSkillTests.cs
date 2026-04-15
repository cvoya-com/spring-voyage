// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.GraphQL;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

public class GetProjectV2ItemSkillTests
{
    [Fact]
    public async Task ExecuteAsync_IssueContent_ProjectsItem()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var item = new ProjectV2Item(
            Id: "PVTI_1",
            Type: "ISSUE",
            IsArchived: false,
            CreatedAt: "2026-04-01T00:00:00Z",
            UpdatedAt: "2026-04-01T12:00:00Z",
            Content: JsonSerializer.SerializeToElement(new
            {
                __typename = "Issue",
                id = "I_1",
                number = 99,
                title = "Bug",
                url = "https://github.com/acme/r/issues/99",
                state = "OPEN",
                repository = new { nameWithOwner = "acme/r" },
            }),
            FieldValues: new ProjectV2FieldValueConnection([]));

        graphql
            .QueryAsync<GetProjectV2ItemResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetProjectV2ItemResponse(item)));

        var result = await new GetProjectV2ItemSkill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("PVTI_1", TestContext.Current.CancellationToken);

        result.GetProperty("found").GetBoolean().ShouldBeTrue();
        var it = result.GetProperty("item");
        it.GetProperty("item_id").GetString().ShouldBe("PVTI_1");
        it.GetProperty("type").GetString().ShouldBe("ISSUE");
        it.GetProperty("content").GetProperty("kind").GetString().ShouldBe("Issue");
        it.GetProperty("content").GetProperty("number").GetInt32().ShouldBe(99);
    }

    [Fact]
    public async Task ExecuteAsync_MissingNode_ReturnsNotFound()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        graphql
            .QueryAsync<GetProjectV2ItemResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetProjectV2ItemResponse(null)));

        var result = await new GetProjectV2ItemSkill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("PVTI_missing", TestContext.Current.CancellationToken);

        result.GetProperty("found").GetBoolean().ShouldBeFalse();
        result.GetProperty("item_id").GetString().ShouldBe("PVTI_missing");
    }
}