// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.GraphQL;

using Cvoya.Spring.Connector.GitHub.GraphQL;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

public class ListProjectsV2SkillTests
{
    [Fact]
    public async Task ExecuteAsync_TwoProjects_ProjectsSummaries()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var response = new ListProjectsV2Response(
            new ProjectsV2Owner(
                Login: "acme",
                ProjectsV2: new ProjectV2Connection(
                [
                    new ProjectV2Summary(
                        Id: "PVT_1",
                        Number: 1,
                        Title: "Delivery Board",
                        Url: "https://github.com/orgs/acme/projects/1",
                        Closed: false,
                        Public: true,
                        ShortDescription: "main",
                        CreatedAt: "2025-01-01T00:00:00Z",
                        UpdatedAt: "2025-02-01T00:00:00Z"),
                    new ProjectV2Summary(
                        Id: "PVT_2",
                        Number: 2,
                        Title: "Closed Board",
                        Url: null,
                        Closed: true,
                        Public: null,
                        ShortDescription: null,
                        CreatedAt: null,
                        UpdatedAt: null),
                ])));

        graphql
            .QueryAsync<ListProjectsV2Response>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var skill = new ListProjectsV2Skill(graphql, NullLoggerFactory.Instance);
        var result = await skill.ExecuteAsync("acme", 30, TestContext.Current.CancellationToken);

        result.GetProperty("owner").GetString().ShouldBe("acme");
        result.GetProperty("owner_exists").GetBoolean().ShouldBeTrue();
        result.GetProperty("project_count").GetInt32().ShouldBe(2);

        var projects = result.GetProperty("projects");
        projects.GetArrayLength().ShouldBe(2);
        projects[0].GetProperty("id").GetString().ShouldBe("PVT_1");
        projects[0].GetProperty("number").GetInt32().ShouldBe(1);
        projects[0].GetProperty("title").GetString().ShouldBe("Delivery Board");
        projects[0].GetProperty("closed").GetBoolean().ShouldBeFalse();
        projects[1].GetProperty("closed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_MissingOwner_ReturnsEmpty()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        graphql
            .QueryAsync<ListProjectsV2Response>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListProjectsV2Response(null)));

        var skill = new ListProjectsV2Skill(graphql, NullLoggerFactory.Instance);
        var result = await skill.ExecuteAsync("nonexistent", 30, TestContext.Current.CancellationToken);

        result.GetProperty("owner_exists").GetBoolean().ShouldBeFalse();
        result.GetProperty("project_count").GetInt32().ShouldBe(0);
        result.GetProperty("projects").GetArrayLength().ShouldBe(0);
    }
}