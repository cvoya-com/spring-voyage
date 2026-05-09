// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Reflection;

using Octokit;

/// <summary>
/// Shared helpers for synthesizing Octokit PR / review / comment response types in tests.
/// Octokit's response types have internal setters and large constructors; reflection-based
/// construction is the least brittle way to populate just the fields a given test cares about.
/// </summary>
internal static class PrTestHelpers
{
    public static PullRequest CreatePullRequest(
        int number,
        string title = "",
        string? body = null,
        ItemState state = ItemState.Open,
        string htmlUrl = "",
        string? nodeId = null,
        string? authorLogin = null,
        string headRef = "feature",
        string headSha = "abc123",
        string baseRef = "main",
        string[]? labels = null,
        string[]? assigneeLogins = null,
        string[]? requestedReviewerLogins = null,
        bool draft = false,
        bool merged = false)
    {
        var ctor = typeof(PullRequest).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name switch
            {
                "number" => number,
                "title" => title,
                "body" => body,
                "state" => state,
                "htmlUrl" => htmlUrl,
                "nodeId" => nodeId,
                "user" => authorLogin != null ? IssueTestHelpers.CreateUser(authorLogin) : null,
                "head" => CreateGitReference(headRef, headSha),
                "base" => CreateGitReference(baseRef, string.Empty),
                "labels" => (labels ?? []).Select(n => IssueTestHelpers.CreateLabel(n)).ToArray(),
                "assignees" => (assigneeLogins ?? []).Select(IssueTestHelpers.CreateUser).ToArray(),
                "requestedReviewers" => (requestedReviewerLogins ?? []).Select(IssueTestHelpers.CreateUser).ToArray(),
                "requestedTeams" => Array.Empty<Team>(),
                "draft" => draft,
                _ => DefaultValue(p.ParameterType),
            };
        }

        var pr = (PullRequest)ctor.Invoke(args);

        if (merged)
        {
            var mergedProp = typeof(PullRequest).GetProperty("Merged");
            mergedProp?.SetValue(pr, true);
        }

        return pr;
    }

    public static PullRequestReview CreateReview(
        long id,
        PullRequestReviewState state,
        string? reviewerLogin = null,
        string body = "",
        DateTimeOffset? submittedAt = null)
    {
        var ctor = typeof(PullRequestReview).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name switch
            {
                "id" => id,
                "state" => state,
                "body" => body,
                "user" => reviewerLogin != null ? IssueTestHelpers.CreateUser(reviewerLogin) : null,
                "submittedAt" => submittedAt ?? DateTimeOffset.UtcNow,
                _ => DefaultValue(p.ParameterType),
            };
        }

        return (PullRequestReview)ctor.Invoke(args);
    }

    public static IssueComment CreateComment(
        long id,
        string body = "",
        string? authorLogin = null,
        string htmlUrl = "",
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        var ctor = typeof(IssueComment).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name switch
            {
                "id" => id,
                "body" => body,
                "user" => authorLogin != null ? IssueTestHelpers.CreateUser(authorLogin) : null,
                "htmlUrl" => htmlUrl,
                "createdAt" => createdAt ?? DateTimeOffset.UtcNow,
                "updatedAt" => updatedAt,
                _ => DefaultValue(p.ParameterType),
            };
        }

        return (IssueComment)ctor.Invoke(args);
    }

    public static PullRequestReviewComment CreateReviewComment(
        long id,
        string body,
        string path,
        int? position = null,
        string? authorLogin = null)
    {
        var ctor = typeof(PullRequestReviewComment).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name switch
            {
                "id" => id,
                "body" => body,
                "path" => path,
                "position" => (int?)position,
                "user" => authorLogin != null ? IssueTestHelpers.CreateUser(authorLogin) : null,
                _ => DefaultValue(p.ParameterType),
            };
        }

        return (PullRequestReviewComment)ctor.Invoke(args);
    }

    public static PullRequestMerge CreateMergeResult(bool merged, string sha = "", string message = "")
    {
        var ctor = typeof(PullRequestMerge).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name switch
            {
                "merged" => merged,
                "sha" => sha,
                "message" => message,
                _ => DefaultValue(p.ParameterType),
            };
        }

        return (PullRequestMerge)ctor.Invoke(args);
    }

    public static SearchIssuesResult CreateSearchResult(int totalCount, Issue[] items, bool incomplete = false)
    {
        var ctor = typeof(SearchIssuesResult).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name switch
            {
                "totalCount" => totalCount,
                "incompleteResults" => incomplete,
                "items" => (IReadOnlyList<Issue>)items,
                _ => DefaultValue(p.ParameterType),
            };
        }

        return (SearchIssuesResult)ctor.Invoke(args);
    }

    private static GitReference CreateGitReference(string @ref, string sha)
    {
        var ctor = typeof(GitReference).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name switch
            {
                "ref" => @ref,
                "sha" => sha,
                _ => DefaultValue(p.ParameterType),
            };
        }

        return (GitReference)ctor.Invoke(args);
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
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var element = t.GetGenericArguments()[0];
            return Array.CreateInstance(element, 0);
        }
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }
}
