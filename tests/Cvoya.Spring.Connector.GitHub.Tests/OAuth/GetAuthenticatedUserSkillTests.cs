// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.OAuth;

using System.Reflection;

using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class GetAuthenticatedUserSkillTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsProjectedProfile()
    {
        var gh = Substitute.For<IGitHubClient>();
        gh.User.Current().Returns(Task.FromResult(BuildUser("octocat", 42, "Octo Cat", "octo@example.com")));

        var factory = Substitute.For<IGitHubOAuthClientFactory>();
        factory.CreateAsync("sess-1", Arg.Any<CancellationToken>()).Returns(gh);

        var skill = new GetAuthenticatedUserSkill(factory, NullLoggerFactory.Instance);

        var result = await skill.ExecuteAsync("sess-1", TestContext.Current.CancellationToken);

        result.GetProperty("login").GetString().ShouldBe("octocat");
        result.GetProperty("id").GetInt64().ShouldBe(42L);
        result.GetProperty("name").GetString().ShouldBe("Octo Cat");
        result.GetProperty("email").GetString().ShouldBe("octo@example.com");
    }

    private static User BuildUser(string login, long id, string name, string email)
    {
        // Octokit's User constructors drift between versions; pick the
        // longest one and fill it reflectively — the same pattern as
        // IssueTestHelpers uses for Issue / Label.
        var ctor = typeof(User).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name switch
            {
                "login" => login,
                "id" => id,
                "name" => name,
                "email" => email,
                _ => DefaultValue(p.ParameterType),
            };
        }
        return (User)ctor.Invoke(args);
    }

    private static object? DefaultValue(Type t)
    {
        if (t == typeof(string))
        {
            return string.Empty;
        }
        if (t == typeof(DateTimeOffset))
        {
            return DateTimeOffset.UtcNow;
        }
        if (t == typeof(DateTimeOffset?))
        {
            return null;
        }
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }
}