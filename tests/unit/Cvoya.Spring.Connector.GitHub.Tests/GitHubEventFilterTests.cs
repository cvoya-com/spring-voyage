// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Webhooks;

using Shouldly;

using Xunit;

/// <summary>
/// Pure-evaluator tests for <see cref="GitHubEventFilter"/>. Issue #2407.
/// </summary>
public class GitHubEventFilterTests
{
    [Fact]
    public void Evaluate_NoFiltersConfigured_Allows()
    {
        var config = NewConfig();
        var payload = IssuePayload(labels: new[] { "bug" }, author: "alice");

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_ExcludeLabel_ShortCircuitsDrop()
    {
        var config = NewConfig(excludeLabels: new[] { "wip" });
        var payload = IssuePayload(labels: new[] { "wip", "feature" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("exclude_label");
        result.Value.ShouldBe("wip");
    }

    [Fact]
    public void Evaluate_ExcludeLabel_NotPresent_DoesNotDrop()
    {
        var config = NewConfig(excludeLabels: new[] { "wip" });
        var payload = IssuePayload(labels: new[] { "bug" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_ExcludeLabel_BeatsIncludeLabel_OnConflict()
    {
        // ExcludeLabels must short-circuit before IncludeLabels — issue #2407.
        var config = NewConfig(
            includeLabels: new[] { "spring-voyage" },
            excludeLabels: new[] { "wip" });
        var payload = IssuePayload(labels: new[] { "wip", "spring-voyage" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("exclude_label");
    }

    [Fact]
    public void Evaluate_IncludeLabel_AtLeastOneMatches_Allows()
    {
        // Disjunctive within a kind.
        var config = NewConfig(includeLabels: new[] { "spring-voyage", "platform" });
        var payload = IssuePayload(labels: new[] { "platform" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_IncludeLabel_NoneMatches_Drops()
    {
        var config = NewConfig(includeLabels: new[] { "spring-voyage" });
        var payload = IssuePayload(labels: new[] { "bug" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("include_label");
        result.Value.ShouldBe("spring-voyage");
    }

    [Fact]
    public void Evaluate_IncludeLabel_NoLabelsOnIssue_Drops()
    {
        var config = NewConfig(includeLabels: new[] { "spring-voyage" });
        var payload = IssuePayload(labels: Array.Empty<string>());

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("include_label");
    }

    [Fact]
    public void Evaluate_IncludeAuthor_Matches_Allows()
    {
        var config = NewConfig(includeAuthors: new[] { "alice", "bob" });
        var payload = IssuePayload(author: "bob");

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_IncludeAuthor_DoesNotMatch_Drops()
    {
        var config = NewConfig(includeAuthors: new[] { "alice" });
        var payload = IssuePayload(author: "bob");

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("include_author");
        result.Value.ShouldBe("alice");
    }

    [Fact]
    public void Evaluate_IncludeAuthor_PullRequestShape_ResolvesPrAuthor()
    {
        var config = NewConfig(includeAuthors: new[] { "alice" });
        var payload = PullRequestPayload(author: "alice");

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_IncludeAuthor_IssueCommentShape_ResolvesCommentAuthor()
    {
        // For issue_comment events, "author" semantically means "who triggered
        // this event" — the comment author, not the original issue author.
        var config = NewConfig(includeAuthors: new[] { "alice" });
        var payload = CommentPayload(commentAuthor: "alice", issueAuthor: "bob");

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_IncludePath_PrShapeMatches_Allows()
    {
        var config = NewConfig(includePaths: new[] { "docs/" });
        var payload = PullRequestPayloadWithFiles("docs/foo.md", "src/Foo.cs");

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_IncludePath_PrShapeDoesNotMatch_Drops()
    {
        var config = NewConfig(includePaths: new[] { "docs/" });
        var payload = PullRequestPayloadWithFiles("src/Foo.cs");

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("include_path");
        result.Value.ShouldBe("docs/");
    }

    [Fact]
    public void Evaluate_IncludePath_PrShapeNoFiles_Drops()
    {
        // PR-shape but no changed files observed — path filter can't be
        // satisfied so the event drops.
        var config = NewConfig(includePaths: new[] { "docs/" });
        var payload = PullRequestPayload(author: "alice");

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("include_path");
    }

    [Fact]
    public void Evaluate_IncludePath_IssueShape_IgnoresPathFilter()
    {
        // Pure issue events have no changed-files surface. The path filter
        // is silently skipped — operators set it intending to gate PRs, not
        // to drop every issue when paths are configured.
        var config = NewConfig(includePaths: new[] { "docs/" });
        var payload = IssuePayload(labels: new[] { "bug" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_CombinedFilters_AllPass_Allows()
    {
        // Conjunctive across kinds — label AND author.
        var config = NewConfig(
            includeLabels: new[] { "spring-voyage" },
            includeAuthors: new[] { "alice" });
        var payload = IssuePayload(labels: new[] { "spring-voyage" }, author: "alice");

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_CombinedFilters_LabelPassesButAuthorFails_Drops()
    {
        var config = NewConfig(
            includeLabels: new[] { "spring-voyage" },
            includeAuthors: new[] { "alice" });
        var payload = IssuePayload(labels: new[] { "spring-voyage" }, author: "carol");

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("include_author");
    }

    [Fact]
    public void Evaluate_IncludeLabel_CaseInsensitive()
    {
        var config = NewConfig(includeLabels: new[] { "Spring-Voyage" });
        var payload = IssuePayload(labels: new[] { "spring-voyage" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_EmptyListSameAsNull_NoFilter()
    {
        var config = NewConfig(
            includeLabels: Array.Empty<string>(),
            excludeLabels: Array.Empty<string>(),
            includeAuthors: Array.Empty<string>(),
            includePaths: Array.Empty<string>());
        var payload = IssuePayload(labels: new[] { "bug" }, author: "alice");

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    private static UnitGitHubConfig NewConfig(
        IReadOnlyList<string>? includeLabels = null,
        IReadOnlyList<string>? excludeLabels = null,
        IReadOnlyList<string>? includeAuthors = null,
        IReadOnlyList<string>? includePaths = null) =>
        new(
            Owner: "acme",
            Repo: "platform",
            IncludeLabels: includeLabels,
            ExcludeLabels: excludeLabels,
            IncludeAuthors: includeAuthors,
            IncludePaths: includePaths);

    private static JsonElement IssuePayload(
        IReadOnlyList<string>? labels = null,
        string author = "opener")
    {
        var data = new
        {
            source = "github",
            intent = "work_assignment",
            action = "opened",
            issue = new
            {
                number = 42,
                title = "T",
                labels = labels ?? Array.Empty<string>(),
                author,
            },
        };
        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement PullRequestPayload(string author)
    {
        var data = new
        {
            source = "github",
            intent = "review_request",
            action = "opened",
            pull_request = new
            {
                number = 10,
                title = "T",
                author,
            },
        };
        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement PullRequestPayloadWithFiles(params string[] files)
    {
        var data = new
        {
            source = "github",
            intent = "review_request",
            action = "opened",
            pull_request = new
            {
                number = 10,
                title = "T",
                author = "alice",
            },
            files,
        };
        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement CommentPayload(string commentAuthor, string issueAuthor)
    {
        var data = new
        {
            source = "github",
            intent = "feedback",
            action = "created",
            issue = new
            {
                number = 42,
                title = "T",
                author = issueAuthor,
            },
            comment = new
            {
                id = 1,
                body = "x",
                author = commentAuthor,
            },
        };
        return JsonSerializer.SerializeToElement(data);
    }
}
