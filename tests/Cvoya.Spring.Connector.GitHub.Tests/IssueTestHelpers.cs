// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Reflection;

using Octokit;

/// <summary>
/// Shared helpers for synthesizing Octokit <see cref="Issue"/> instances in tests.
/// Octokit's response types have internal setters and large constructors; reflection-based
/// construction is the least brittle way to populate just the fields a given test cares about.
/// </summary>
internal static class IssueTestHelpers
{
    public static Issue CreateIssue(
        int number,
        string title = "",
        string? body = null,
        ItemState state = ItemState.Open,
        string htmlUrl = "",
        string? authorLogin = null,
        string[]? labelNames = null,
        string[]? assigneeLogins = null)
    {
        var ctor = typeof(Issue).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            args[i] = param.Name switch
            {
                "number" => number,
                "title" => title,
                "body" => body,
                "state" => state,
                "htmlUrl" => htmlUrl,
                "user" => authorLogin != null ? CreateUser(authorLogin) : null,
                "labels" => (labelNames ?? []).Select(n => CreateLabel(n)).ToArray(),
                "assignees" => (assigneeLogins ?? []).Select(CreateUser).ToArray(),
                "pullRequest" => null,
                _ => DefaultValue(param.ParameterType),
            };
        }

        return (Issue)ctor.Invoke(args);
    }

    public static User CreateUser(string login)
    {
        var ctor = typeof(User).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name == "login" ? login : DefaultValue(p.ParameterType);
        }
        return (User)ctor.Invoke(args);
    }

    public static Label CreateLabel(string name)
    {
        var ctor = typeof(Label).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name switch
            {
                "name" => name,
                "color" => "ededed",
                _ => DefaultValue(p.ParameterType),
            };
        }
        return (Label)ctor.Invoke(args);
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