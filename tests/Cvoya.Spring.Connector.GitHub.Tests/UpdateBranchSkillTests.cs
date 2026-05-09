// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Net;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;
using Octokit.Internal;

using Shouldly;

using Xunit;

public class UpdateBranchSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly IConnection _connection;
    private readonly UpdateBranchSkill _skill;

    public UpdateBranchSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        _connection = Substitute.For<IConnection>();
        _gitHubClient.Connection.Returns(_connection);

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new UpdateBranchSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_PostsToUpdateBranchEndpoint_IncludesExpectedSha()
    {
        Uri? capturedUri = null;
        object? capturedBody = null;

        var response = Substitute.For<IResponse>();
        response.StatusCode.Returns(HttpStatusCode.Accepted);
        var apiResponse = new ApiResponse<UpdateBranchSkill.UpdateBranchResponse>(
            response,
            new UpdateBranchSkill.UpdateBranchResponse { Message = "Updating pull request branch.", Url = "https://api.github.com/..." });

        _connection
            .Put<UpdateBranchSkill.UpdateBranchResponse>(
                Arg.Do<Uri>(u => capturedUri = u),
                Arg.Do<object>(b => capturedBody = b))
            .Returns(apiResponse);

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 42, expectedHeadSha: "abc123",
            TestContext.Current.CancellationToken);

        capturedUri.ShouldNotBeNull();
        capturedUri!.ToString().ShouldBe("repos/owner/repo/pulls/42/update-branch");

        capturedBody.ShouldNotBeNull();
        var dict = capturedBody as Dictionary<string, string>;
        dict.ShouldNotBeNull();
        dict!["expected_head_sha"].ShouldBe("abc123");

        result.GetProperty("updated").GetBoolean().ShouldBeTrue();
        result.GetProperty("status_code").GetInt32().ShouldBe((int)HttpStatusCode.Accepted);
        result.GetProperty("message").GetString().ShouldBe("Updating pull request branch.");
    }

    [Fact]
    public async Task ExecuteAsync_NoExpectedSha_SendsEmptyBody()
    {
        object? capturedBody = null;
        var response = Substitute.For<IResponse>();
        response.StatusCode.Returns(HttpStatusCode.Accepted);
        var apiResponse = new ApiResponse<UpdateBranchSkill.UpdateBranchResponse>(
            response,
            new UpdateBranchSkill.UpdateBranchResponse { Message = "ok" });

        _connection
            .Put<UpdateBranchSkill.UpdateBranchResponse>(Arg.Any<Uri>(), Arg.Do<object>(b => capturedBody = b))
            .Returns(apiResponse);

        await _skill.ExecuteAsync(
            "owner", "repo", 42, expectedHeadSha: null,
            TestContext.Current.CancellationToken);

        var dict = capturedBody as Dictionary<string, string>;
        dict.ShouldNotBeNull();
        dict!.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ValidationException_ReturnsUpdatedFalse()
    {
        _connection
            .Put<UpdateBranchSkill.UpdateBranchResponse>(Arg.Any<Uri>(), Arg.Any<object>())
            .Returns<IApiResponse<UpdateBranchSkill.UpdateBranchResponse>>(_ =>
                throw new ApiValidationException());

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 42, expectedHeadSha: null,
            TestContext.Current.CancellationToken);

        result.GetProperty("updated").GetBoolean().ShouldBeFalse();
        result.GetProperty("status_code").GetInt32().ShouldBe((int)HttpStatusCode.UnprocessableEntity);
    }
}
