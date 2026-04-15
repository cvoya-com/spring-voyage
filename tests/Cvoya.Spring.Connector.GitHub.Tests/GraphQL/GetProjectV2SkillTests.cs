// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.GraphQL;

using Cvoya.Spring.Connector.GitHub.GraphQL;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

public class GetProjectV2SkillTests
{
    [Fact]
    public async Task ExecuteAsync_WithFields_ProjectsSingleSelectAndIterationConfig()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var fields = new ProjectV2FieldConnection(
        [
            new ProjectV2FieldDefinition(
                Id: "F_TITLE",
                Name: "Title",
                DataType: "TITLE",
                Options: null,
                Configuration: null),
            new ProjectV2FieldDefinition(
                Id: "F_STATUS",
                Name: "Status",
                DataType: "SINGLE_SELECT",
                Options:
                [
                    new ProjectV2SingleSelectOption("OPT_T", "Todo"),
                    new ProjectV2SingleSelectOption("OPT_D", "Done"),
                ],
                Configuration: null),
            new ProjectV2FieldDefinition(
                Id: "F_ITER",
                Name: "Iteration",
                DataType: "ITERATION",
                Options: null,
                Configuration: new ProjectV2IterationConfiguration(
                    Duration: 14,
                    StartDay: 1,
                    Iterations: [new ProjectV2Iteration("IT_1", "Sprint 1", "2026-04-01", 14)],
                    CompletedIterations: [new ProjectV2Iteration("IT_0", "Sprint 0", "2026-03-01", 14)])),
        ]);

        var response = new GetProjectV2Response(
            new ProjectV2OwnerWithProject(
                Login: "acme",
                ProjectV2: new ProjectV2Detail(
                    Id: "PVT_1",
                    Number: 1,
                    Title: "Delivery Board",
                    Url: "https://github.com/orgs/acme/projects/1",
                    Closed: false,
                    Public: true,
                    ShortDescription: "main",
                    Readme: null,
                    CreatedAt: null,
                    UpdatedAt: null,
                    Fields: fields)));

        graphql
            .QueryAsync<GetProjectV2Response>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var result = await new GetProjectV2Skill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("acme", 1, TestContext.Current.CancellationToken);

        result.GetProperty("found").GetBoolean().ShouldBeTrue();
        result.GetProperty("project").GetProperty("id").GetString().ShouldBe("PVT_1");
        result.GetProperty("field_count").GetInt32().ShouldBe(3);

        var fieldsJson = result.GetProperty("fields");
        fieldsJson.GetArrayLength().ShouldBe(3);
        fieldsJson[1].GetProperty("data_type").GetString().ShouldBe("SINGLE_SELECT");
        fieldsJson[1].GetProperty("options").GetArrayLength().ShouldBe(2);
        fieldsJson[1].GetProperty("options")[0].GetProperty("name").GetString().ShouldBe("Todo");
        fieldsJson[2].GetProperty("iteration_configuration").GetProperty("duration").GetInt32().ShouldBe(14);
        fieldsJson[2].GetProperty("iteration_configuration").GetProperty("iterations").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownProject_ReturnsNotFound()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        graphql
            .QueryAsync<GetProjectV2Response>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetProjectV2Response(new ProjectV2OwnerWithProject("acme", null))));

        var result = await new GetProjectV2Skill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("acme", 999, TestContext.Current.CancellationToken);

        result.GetProperty("found").GetBoolean().ShouldBeFalse();
    }
}