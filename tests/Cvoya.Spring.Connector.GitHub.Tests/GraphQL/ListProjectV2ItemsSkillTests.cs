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

public class ListProjectV2ItemsSkillTests
{
    [Fact]
    public async Task ExecuteAsync_EmptyProject_ReturnsEmptyItems()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var response = new ListProjectV2ItemsResponse(
            new ProjectV2OwnerWithItems(
                new ProjectV2WithItems(
                    Id: "PVT_1",
                    Number: 1,
                    Title: "Delivery Board",
                    Items: new ProjectV2ItemConnection(
                        PageInfo: new ProjectV2PageInfo(EndCursor: null, HasNextPage: false),
                        Nodes: []))));
        graphql
            .QueryAsync<ListProjectV2ItemsResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var result = await new ListProjectV2ItemsSkill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("acme", 1, null, 50, TestContext.Current.CancellationToken);

        result.GetProperty("found").GetBoolean().ShouldBeTrue();
        result.GetProperty("item_count").GetInt32().ShouldBe(0);
        result.GetProperty("has_next_page").GetBoolean().ShouldBeFalse();
        result.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_Pagination_RoundTripsCursor()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();

        // First page: end_cursor=C1, has_next_page=true
        var page1 = new ListProjectV2ItemsResponse(
            new ProjectV2OwnerWithItems(
                new ProjectV2WithItems("PVT_1", 1, "Board", new ProjectV2ItemConnection(
                    PageInfo: new ProjectV2PageInfo(EndCursor: "C1", HasNextPage: true),
                    Nodes: [MakeIssueItem("I_1", 1, "first")]))));
        // Second page: end_cursor=C2, has_next_page=false
        var page2 = new ListProjectV2ItemsResponse(
            new ProjectV2OwnerWithItems(
                new ProjectV2WithItems("PVT_1", 1, "Board", new ProjectV2ItemConnection(
                    PageInfo: new ProjectV2PageInfo(EndCursor: "C2", HasNextPage: false),
                    Nodes: [MakeIssueItem("I_2", 2, "second")]))));

        string? capturedCursor = null;
        graphql
            .QueryAsync<ListProjectV2ItemsResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var vars = ci.ArgAt<object?>(1) as IDictionary<string, object?>;
                capturedCursor = vars?["after"] as string;
                return Task.FromResult(capturedCursor is null ? page1 : page2);
            });

        var skill = new ListProjectV2ItemsSkill(graphql, NullLoggerFactory.Instance);

        var first = await skill.ExecuteAsync("acme", 1, null, 50, TestContext.Current.CancellationToken);
        first.GetProperty("end_cursor").GetString().ShouldBe("C1");
        first.GetProperty("has_next_page").GetBoolean().ShouldBeTrue();

        var second = await skill.ExecuteAsync("acme", 1, first.GetProperty("end_cursor").GetString(), 50, TestContext.Current.CancellationToken);
        second.GetProperty("end_cursor").GetString().ShouldBe("C2");
        second.GetProperty("has_next_page").GetBoolean().ShouldBeFalse();

        capturedCursor.ShouldBe("C1");
    }

    [Fact]
    public async Task ExecuteAsync_FieldValueExtraction_AllFiveTypes()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();

        var fieldValuesJson = JsonSerializer.SerializeToElement(new object[]
        {
            // Text
            new
            {
                __typename = "ProjectV2ItemFieldTextValue",
                text = "Needs triage",
                field = new { __typename = "ProjectV2Field", id = "F_TXT", name = "Note", dataType = "TEXT" },
            },
            // Number
            new
            {
                __typename = "ProjectV2ItemFieldNumberValue",
                number = 3.5,
                field = new { __typename = "ProjectV2Field", id = "F_NUM", name = "Estimate", dataType = "NUMBER" },
            },
            // Date
            new
            {
                __typename = "ProjectV2ItemFieldDateValue",
                date = "2026-05-01",
                field = new { __typename = "ProjectV2Field", id = "F_DT", name = "Due", dataType = "DATE" },
            },
            // Single-select
            new
            {
                __typename = "ProjectV2ItemFieldSingleSelectValue",
                optionId = "OPT_T",
                name = "Todo",
                field = new { __typename = "ProjectV2SingleSelectField", id = "F_SS", name = "Status", dataType = "SINGLE_SELECT" },
            },
            // Iteration
            new
            {
                __typename = "ProjectV2ItemFieldIterationValue",
                iterationId = "IT_1",
                title = "Sprint 1",
                startDate = "2026-04-01",
                duration = 14,
                field = new { __typename = "ProjectV2IterationField", id = "F_IT", name = "Iteration", dataType = "ITERATION" },
            },
        });

        var item = new ProjectV2Item(
            Id: "PVTI_1",
            Type: "ISSUE",
            IsArchived: false,
            CreatedAt: "2026-04-01T00:00:00Z",
            UpdatedAt: "2026-04-02T00:00:00Z",
            Content: JsonSerializer.SerializeToElement(new
            {
                __typename = "Issue",
                id = "I_1",
                number = 123,
                title = "Sample",
                url = "https://github.com/acme/repo/issues/123",
                state = "OPEN",
                repository = new { nameWithOwner = "acme/repo" },
            }),
            FieldValues: new ProjectV2FieldValueConnection(
                [.. fieldValuesJson.EnumerateArray()]));

        var response = new ListProjectV2ItemsResponse(
            new ProjectV2OwnerWithItems(
                new ProjectV2WithItems("PVT_1", 1, "Board",
                    new ProjectV2ItemConnection(
                        new ProjectV2PageInfo(null, false),
                        [item]))));

        graphql
            .QueryAsync<ListProjectV2ItemsResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var result = await new ListProjectV2ItemsSkill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("acme", 1, null, 50, TestContext.Current.CancellationToken);

        result.GetProperty("item_count").GetInt32().ShouldBe(1);
        var items = result.GetProperty("items");
        items[0].GetProperty("content").GetProperty("kind").GetString().ShouldBe("Issue");
        items[0].GetProperty("content").GetProperty("number").GetInt32().ShouldBe(123);
        items[0].GetProperty("content").GetProperty("repository").GetString().ShouldBe("acme/repo");

        var values = items[0].GetProperty("field_values");
        values.GetArrayLength().ShouldBe(5);

        values[0].GetProperty("data_type").GetString().ShouldBe("TEXT");
        values[0].GetProperty("text").GetString().ShouldBe("Needs triage");

        values[1].GetProperty("data_type").GetString().ShouldBe("NUMBER");
        values[1].GetProperty("number").GetDouble().ShouldBe(3.5);

        values[2].GetProperty("data_type").GetString().ShouldBe("DATE");
        values[2].GetProperty("date").GetString().ShouldBe("2026-05-01");

        values[3].GetProperty("data_type").GetString().ShouldBe("SINGLE_SELECT");
        values[3].GetProperty("option_id").GetString().ShouldBe("OPT_T");
        values[3].GetProperty("option_name").GetString().ShouldBe("Todo");

        values[4].GetProperty("data_type").GetString().ShouldBe("ITERATION");
        values[4].GetProperty("iteration_id").GetString().ShouldBe("IT_1");
        values[4].GetProperty("iteration_title").GetString().ShouldBe("Sprint 1");
        values[4].GetProperty("iteration_start_date").GetString().ShouldBe("2026-04-01");
        values[4].GetProperty("iteration_duration").GetInt32().ShouldBe(14);
    }

    [Fact]
    public async Task ExecuteAsync_DraftIssueContent_ProjectsKind()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var item = new ProjectV2Item(
            Id: "PVTI_D",
            Type: "DRAFT_ISSUE",
            IsArchived: false,
            CreatedAt: null,
            UpdatedAt: null,
            Content: JsonSerializer.SerializeToElement(new
            {
                __typename = "DraftIssue",
                id = "D_1",
                title = "Draft idea",
                body = "to be refined",
            }),
            FieldValues: new ProjectV2FieldValueConnection([]));

        var response = new ListProjectV2ItemsResponse(
            new ProjectV2OwnerWithItems(
                new ProjectV2WithItems("PVT_1", 1, "Board",
                    new ProjectV2ItemConnection(new ProjectV2PageInfo(null, false), [item]))));
        graphql
            .QueryAsync<ListProjectV2ItemsResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var result = await new ListProjectV2ItemsSkill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("acme", 1, null, 50, TestContext.Current.CancellationToken);

        var content = result.GetProperty("items")[0].GetProperty("content");
        content.GetProperty("kind").GetString().ShouldBe("DraftIssue");
        content.GetProperty("title").GetString().ShouldBe("Draft idea");
    }

    [Fact]
    public async Task ExecuteAsync_MissingProject_ReturnsNotFound()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        graphql
            .QueryAsync<ListProjectV2ItemsResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListProjectV2ItemsResponse(new ProjectV2OwnerWithItems(null))));

        var result = await new ListProjectV2ItemsSkill(graphql, NullLoggerFactory.Instance)
            .ExecuteAsync("acme", 1, null, 50, TestContext.Current.CancellationToken);

        result.GetProperty("found").GetBoolean().ShouldBeFalse();
        result.GetProperty("item_count").GetInt32().ShouldBe(0);
    }

    private static ProjectV2Item MakeIssueItem(string id, int number, string title) => new(
        Id: id,
        Type: "ISSUE",
        IsArchived: false,
        CreatedAt: null,
        UpdatedAt: null,
        Content: JsonSerializer.SerializeToElement(new
        {
            __typename = "Issue",
            id,
            number,
            title,
            url = (string?)null,
            state = "OPEN",
            repository = new { nameWithOwner = "acme/repo" },
        }),
        FieldValues: new ProjectV2FieldValueConnection([]));
}