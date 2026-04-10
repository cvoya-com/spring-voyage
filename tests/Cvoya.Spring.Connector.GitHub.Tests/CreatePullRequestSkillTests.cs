// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Octokit;
using Cvoya.Spring.Connector.GitHub.Skills;
using Xunit;

public class CreatePullRequestSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly CreatePullRequestSkill _skill;

    public CreatePullRequestSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        var logger = Substitute.For<ILogger>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        _skill = new CreatePullRequestSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_CreatesWithCorrectParameters_ReturnsResult()
    {
        var fakePr = CreatePullRequestViaReflection(99, "Fix bug", "https://github.com/owner/repo/pull/99");
        _gitHubClient.PullRequest
            .Create("owner", "repo", Arg.Any<NewPullRequest>())
            .Returns(fakePr);

        var result = await _skill.ExecuteAsync(
            "owner", "repo", "Fix bug", "Fixes the bug", "fix-branch", "main",
            TestContext.Current.CancellationToken);

        result.GetProperty("number").GetInt32().Should().Be(99);
        result.GetProperty("title").GetString().Should().Be("Fix bug");
        result.GetProperty("html_url").GetString().Should().Be("https://github.com/owner/repo/pull/99");
    }

    private static PullRequest CreatePullRequestViaReflection(int number, string title, string htmlUrl)
    {
        // PullRequest has a large constructor; we use the parameterless constructor
        // and set properties via reflection since they have internal setters.
        var ctor = typeof(PullRequest).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            if (param.Name == "number")
                args[i] = number;
            else if (param.Name == "title")
                args[i] = title;
            else if (param.Name == "htmlUrl")
                args[i] = htmlUrl;
            else if (param.Name == "state")
                args[i] = ItemState.Open;
            else if (param.ParameterType == typeof(string))
                args[i] = string.Empty;
            else if (param.ParameterType == typeof(int))
                args[i] = 0;
            else if (param.ParameterType == typeof(long))
                args[i] = 0L;
            else if (param.ParameterType == typeof(bool))
                args[i] = false;
            else if (param.ParameterType == typeof(DateTimeOffset))
                args[i] = DateTimeOffset.UtcNow;
            else if (param.ParameterType.IsValueType)
                args[i] = Activator.CreateInstance(param.ParameterType);
            else
                args[i] = null;
        }

        return (PullRequest)ctor.Invoke(args);
    }
}
