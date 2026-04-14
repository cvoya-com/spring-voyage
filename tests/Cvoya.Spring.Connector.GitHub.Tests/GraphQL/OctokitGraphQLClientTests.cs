// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.GraphQL;

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Octokit;
using Octokit.Internal;

using Shouldly;

using Xunit;

public class OctokitGraphQLClientTests
{
    private sealed record TestData([property: JsonPropertyName("hello")] string Hello);

    private static OctokitGraphQLClient CreateClient(IConnection connection) =>
        new(connection, NullLoggerFactory.Instance);

    // Pre-built response helper — mirroring the EnableAutoMergeSkillTests
    // pattern which is known to work with Octokit's IConnection.Post<T>
    // overload that returns Task<IApiResponse<T>>.
    private static ApiResponse<JsonElement> Envelope(string jsonBody)
    {
        var response = Substitute.For<IResponse>();
        response.StatusCode.Returns(HttpStatusCode.OK);
        return new ApiResponse<JsonElement>(response, JsonSerializer.Deserialize<JsonElement>(jsonBody));
    }

    [Fact]
    public async Task QueryAsync_HappyPath_DeserializesData()
    {
        var connection = Substitute.For<IConnection>();
        Uri? capturedUri = null;
        object? capturedBody = null;

        var envelope = Envelope("""{"data":{"hello":"world"}}""");
        connection
            .Post<JsonElement>(
                Arg.Do<Uri>(u => capturedUri = u),
                Arg.Do<object>(b => capturedBody = b),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(envelope);

        var result = await CreateClient(connection).QueryAsync<TestData>(
            "query { hello }",
            new Dictionary<string, object?> { ["x"] = 1 },
            TestContext.Current.CancellationToken);

        result.Hello.ShouldBe("world");
        capturedUri!.ToString().ShouldBe("graphql");

        var body = (Dictionary<string, object?>)capturedBody!;
        body["query"].ShouldBe("query { hello }");
        var vars = (Dictionary<string, object?>)body["variables"]!;
        vars["x"].ShouldBe(1);
    }

    [Fact]
    public async Task QueryAsync_Errors_ThrowsGraphQLException()
    {
        var connection = Substitute.For<IConnection>();
        var envelope = Envelope("""{"errors":[{"message":"bad thing"},{"message":"other bad thing"}]}""");
        connection
            .Post<JsonElement>(Arg.Any<Uri>(), Arg.Any<object>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(envelope);

        var ex = await Should.ThrowAsync<GitHubGraphQLException>(() =>
            CreateClient(connection).QueryAsync<TestData>("q", null, TestContext.Current.CancellationToken));
        ex.Errors.ShouldBe(["bad thing", "other bad thing"]);
        ex.Message.ShouldContain("bad thing");
    }

    [Fact]
    public async Task QueryAsync_MissingData_Throws()
    {
        var connection = Substitute.For<IConnection>();
        var envelope = Envelope("""{}""");
        connection
            .Post<JsonElement>(Arg.Any<Uri>(), Arg.Any<object>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(envelope);

        await Should.ThrowAsync<GitHubGraphQLException>(() =>
            CreateClient(connection).QueryAsync<TestData>("q", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueryAsync_AcceptsAnonymousVariableObject()
    {
        var connection = Substitute.For<IConnection>();
        object? captured = null;
        var envelope = Envelope("""{"data":{"hello":"hi"}}""");
        connection
            .Post<JsonElement>(Arg.Any<Uri>(), Arg.Do<object>(b => captured = b), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(envelope);

        var _ = await CreateClient(connection).QueryAsync<TestData>(
            "q",
            new { owner = "o", repo = "r", number = 1 },
            TestContext.Current.CancellationToken);

        var vars = (Dictionary<string, object?>)((Dictionary<string, object?>)captured!)["variables"]!;
        vars["owner"].ShouldBe("o");
        vars["repo"].ShouldBe("r");
        vars["number"].ShouldBe(1);
    }

    [Fact]
    public async Task MutateAsync_HappyPath_DeserializesData()
    {
        var connection = Substitute.For<IConnection>();
        var envelope = Envelope("""{"data":{"hello":"mutated"}}""");
        connection
            .Post<JsonElement>(Arg.Any<Uri>(), Arg.Any<object>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(envelope);

        var result = await CreateClient(connection).MutateAsync<TestData>(
            "mutation { hello }", null, TestContext.Current.CancellationToken);

        result.Hello.ShouldBe("mutated");
    }

    [Fact]
    public async Task QueryAsync_JsonElement_ReturnsRawData()
    {
        var connection = Substitute.For<IConnection>();
        var envelope = Envelope("""{"data":{"foo":42}}""");
        connection
            .Post<JsonElement>(Arg.Any<Uri>(), Arg.Any<object>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(envelope);

        var data = await CreateClient(connection).QueryAsync<JsonElement>(
            "q", null, TestContext.Current.CancellationToken);
        data.GetProperty("foo").GetInt32().ShouldBe(42);
    }

    [Fact]
    public async Task QueryAsync_EmptyQuery_Throws()
    {
        var connection = Substitute.For<IConnection>();
        await Should.ThrowAsync<ArgumentException>(() =>
            CreateClient(connection).QueryAsync<TestData>("", null, TestContext.Current.CancellationToken));
    }
}