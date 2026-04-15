// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.GraphQL;

using System.Collections.Generic;

using Cvoya.Spring.Connector.GitHub.Caching;
using Cvoya.Spring.Connector.GitHub.GraphQL;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

public class UpdateProjectV2ItemFieldValueSkillTests
{
    private static (IGitHubGraphQLClient Graphql, IGitHubResponseCache Cache, List<Dictionary<string, object?>> CapturedInputs) SetupSuccessfulClient()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var cache = Substitute.For<IGitHubResponseCache>();

        var updated = new ProjectV2Item(
            Id: "PVTI_1", Type: "ISSUE", IsArchived: false,
            CreatedAt: null, UpdatedAt: "2026-04-13T12:00:00Z",
            Content: null, FieldValues: null);

        var captured = new List<Dictionary<string, object?>>();

        graphql
            .MutateAsync<UpdateProjectV2ItemFieldValueResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var variables = (Dictionary<string, object?>)ci.ArgAt<object?>(1)!;
                var input = (Dictionary<string, object?>)variables["input"]!;
                captured.Add(input);
                return Task.FromResult(new UpdateProjectV2ItemFieldValueResponse(
                    new UpdateProjectV2ItemFieldValuePayload(updated)));
            });

        return (graphql, cache, captured);
    }

    [Fact]
    public async Task ExecuteAsync_TextValue_SendsTextInputAndInvalidatesBothTags()
    {
        var (graphql, cache, captured) = SetupSuccessfulClient();

        var skill = new UpdateProjectV2ItemFieldValueSkill(graphql, cache, NullLoggerFactory.Instance);
        var result = await skill.ExecuteAsync(
            "PVT_1", "PVTI_1", "PVTF_t",
            valueType: "text",
            textValue: "hello",
            owner: "acme", number: 7,
            cancellationToken: TestContext.Current.CancellationToken);

        result.GetProperty("updated").GetBoolean().ShouldBeTrue();
        result.GetProperty("value_type").GetString().ShouldBe("text");

        captured.Count.ShouldBe(1);
        var value = (Dictionary<string, object?>)captured[0]["value"]!;
        value.ShouldContainKey("text");
        value["text"].ShouldBe("hello");

        await cache.Received(1).InvalidateByTagAsync("project-v2-item:PVTI_1", Arg.Any<CancellationToken>());
        await cache.Received(1).InvalidateByTagAsync("project-v2:acme/7", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NumberValue_SendsNumberInput()
    {
        var (graphql, cache, captured) = SetupSuccessfulClient();

        var skill = new UpdateProjectV2ItemFieldValueSkill(graphql, cache, NullLoggerFactory.Instance);
        await skill.ExecuteAsync(
            "PVT_1", "PVTI_1", "PVTF_n",
            valueType: "number",
            numberValue: 42.5,
            cancellationToken: TestContext.Current.CancellationToken);

        var value = (Dictionary<string, object?>)captured[0]["value"]!;
        value["number"].ShouldBe(42.5);
    }

    [Fact]
    public async Task ExecuteAsync_DateValue_SendsDateInput()
    {
        var (graphql, cache, captured) = SetupSuccessfulClient();

        var skill = new UpdateProjectV2ItemFieldValueSkill(graphql, cache, NullLoggerFactory.Instance);
        await skill.ExecuteAsync(
            "PVT_1", "PVTI_1", "PVTF_d",
            valueType: "date",
            dateValue: "2026-04-13",
            cancellationToken: TestContext.Current.CancellationToken);

        var value = (Dictionary<string, object?>)captured[0]["value"]!;
        value["date"].ShouldBe("2026-04-13");
    }

    [Fact]
    public async Task ExecuteAsync_SingleSelectValue_SendsOptionId()
    {
        var (graphql, cache, captured) = SetupSuccessfulClient();

        var skill = new UpdateProjectV2ItemFieldValueSkill(graphql, cache, NullLoggerFactory.Instance);
        await skill.ExecuteAsync(
            "PVT_1", "PVTI_1", "PVTF_s",
            valueType: "single_select",
            singleSelectOptionId: "OPT_1",
            cancellationToken: TestContext.Current.CancellationToken);

        var value = (Dictionary<string, object?>)captured[0]["value"]!;
        value["singleSelectOptionId"].ShouldBe("OPT_1");
    }

    [Fact]
    public async Task ExecuteAsync_IterationValue_SendsIterationId()
    {
        var (graphql, cache, captured) = SetupSuccessfulClient();

        var skill = new UpdateProjectV2ItemFieldValueSkill(graphql, cache, NullLoggerFactory.Instance);
        await skill.ExecuteAsync(
            "PVT_1", "PVTI_1", "PVTF_i",
            valueType: "iteration",
            iterationId: "ITR_1",
            cancellationToken: TestContext.Current.CancellationToken);

        var value = (Dictionary<string, object?>)captured[0]["value"]!;
        value["iterationId"].ShouldBe("ITR_1");
    }

    [Fact]
    public async Task ExecuteAsync_MissingValue_ThrowsArgumentException()
    {
        var (graphql, cache, _) = SetupSuccessfulClient();
        var skill = new UpdateProjectV2ItemFieldValueSkill(graphql, cache, NullLoggerFactory.Instance);

        await Should.ThrowAsync<ArgumentException>(() => skill.ExecuteAsync(
            "PVT_1", "PVTI_1", "PVTF_t",
            valueType: "text",
            cancellationToken: TestContext.Current.CancellationToken));

        await cache.DidNotReceive().InvalidateByTagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UnknownValueType_Throws()
    {
        var (graphql, cache, _) = SetupSuccessfulClient();
        var skill = new UpdateProjectV2ItemFieldValueSkill(graphql, cache, NullLoggerFactory.Instance);

        await Should.ThrowAsync<ArgumentException>(() => skill.ExecuteAsync(
            "PVT_1", "PVTI_1", "PVTF_t",
            valueType: "bool",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteAsync_GraphQLError_DoesNotInvalidate()
    {
        var graphql = Substitute.For<IGitHubGraphQLClient>();
        var cache = Substitute.For<IGitHubResponseCache>();

        graphql
            .MutateAsync<UpdateProjectV2ItemFieldValueResponse>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns<Task<UpdateProjectV2ItemFieldValueResponse>>(_ => throw new GitHubGraphQLException(["forbidden"]));

        var skill = new UpdateProjectV2ItemFieldValueSkill(graphql, cache, NullLoggerFactory.Instance);

        await Should.ThrowAsync<GitHubGraphQLException>(() => skill.ExecuteAsync(
            "PVT_1", "PVTI_1", "PVTF_t",
            valueType: "text", textValue: "x",
            owner: "acme", number: 7,
            cancellationToken: TestContext.Current.CancellationToken));

        await cache.DidNotReceive().InvalidateByTagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}