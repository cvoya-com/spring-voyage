// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Webhooks;

using Shouldly;

using Xunit;

/// <summary>
/// Pure-evaluator tests for <see cref="GitHubEventFilter"/>. Issue #2407;
/// wildcard label patterns and PR / issue-comment label sourcing per
/// issue #2563.
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

    // ---- Wildcard pattern matching (issue #2563) ----

    [Fact]
    public void Evaluate_IncludeLabel_StarPattern_MatchesAnyLabel()
    {
        var config = NewConfig(includeLabels: new[] { "*" });
        var payload = IssuePayload(labels: new[] { "anything" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_IncludeLabel_StarPattern_NoLabelsOnIssue_Drops()
    {
        // "*" only matches when at least one label is present — a label-less
        // event still fails the include filter.
        var config = NewConfig(includeLabels: new[] { "*" });
        var payload = IssuePayload(labels: Array.Empty<string>());

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("include_label");
    }

    [Fact]
    public void Evaluate_IncludeLabel_PrefixPattern_MatchesNamespacedLabel()
    {
        var config = NewConfig(includeLabels: new[] { "spring-voyage-team:*" });
        var payload = IssuePayload(labels: new[] { "spring-voyage-team:platform" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_IncludeLabel_PrefixPattern_NoMatch_Drops()
    {
        var config = NewConfig(includeLabels: new[] { "spring-voyage-team:*" });
        var payload = IssuePayload(labels: new[] { "other:thing", "bug" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("include_label");
    }

    [Fact]
    public void Evaluate_IncludeLabel_PrefixPattern_DoesNotMatchPrefixWithoutSeparator()
    {
        // "area:*" matches "area:platform" but NOT the bare label "area"
        // (the trailing ':' is part of the pattern).
        var config = NewConfig(includeLabels: new[] { "area:*" });
        var payload = IssuePayload(labels: new[] { "area" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("include_label");
    }

    [Fact]
    public void Evaluate_IncludeLabel_PrefixPattern_CaseInsensitive()
    {
        var config = NewConfig(includeLabels: new[] { "Area:*" });
        var payload = IssuePayload(labels: new[] { "area:Platform" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_ExcludeLabel_StarPattern_DropsAnyLabel()
    {
        var config = NewConfig(excludeLabels: new[] { "*" });
        var payload = IssuePayload(labels: new[] { "anything" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("exclude_label");
        result.Value.ShouldBe("anything");
    }

    [Fact]
    public void Evaluate_ExcludeLabel_PrefixPattern_DropsNamespacedLabel()
    {
        // The exclude payload is reported with the actual label (not the
        // pattern) so operators see what came in on the wire.
        var config = NewConfig(excludeLabels: new[] { "internal:*" });
        var payload = IssuePayload(labels: new[] { "internal:do-not-route", "bug" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("exclude_label");
        result.Value.ShouldBe("internal:do-not-route");
    }

    [Fact]
    public void Evaluate_ExcludeLabel_PrefixPattern_OtherLabelsPresent_DoesNotDrop()
    {
        var config = NewConfig(excludeLabels: new[] { "internal:*" });
        var payload = IssuePayload(labels: new[] { "bug", "feature" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_IncludeLabel_PrefixAllowsButExcludeMatchesSpecific_Drops()
    {
        // Realistic compose: allow the namespace, block one specific label
        // inside it.
        var config = NewConfig(
            includeLabels: new[] { "spring-voyage-team:*" },
            excludeLabels: new[] { "spring-voyage-team:wip" });
        var payload = IssuePayload(labels: new[] { "spring-voyage-team:wip" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("exclude_label");
    }

    [Fact]
    public void Evaluate_StarPattern_CombinedWithSpecificPattern_MatchesEither()
    {
        var config = NewConfig(includeLabels: new[] { "bug", "spring-voyage-team:*" });
        var payload = IssuePayload(labels: new[] { "spring-voyage-team:eng" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    // ---- PR-shape label sourcing (issue #2563) ----

    [Fact]
    public void Evaluate_IncludeLabel_PullRequestShape_ReadsLabelsFromPullRequest()
    {
        var config = NewConfig(includeLabels: new[] { "spring-voyage" });
        var payload = PullRequestPayloadWithLabels(
            author: "alice",
            labels: new[] { "spring-voyage", "review-needed" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_ExcludeLabel_PullRequestShape_DropsOnPrLabel()
    {
        var config = NewConfig(excludeLabels: new[] { "wip" });
        var payload = PullRequestPayloadWithLabels(
            author: "alice",
            labels: new[] { "wip" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("exclude_label");
        result.Value.ShouldBe("wip");
    }

    [Fact]
    public void Evaluate_IncludeLabel_PullRequestShape_NoMatch_Drops()
    {
        var config = NewConfig(includeLabels: new[] { "spring-voyage" });
        var payload = PullRequestPayloadWithLabels(
            author: "alice",
            labels: new[] { "other" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("include_label");
    }

    [Fact]
    public void Evaluate_IncludeLabel_IssueCommentShape_ReadsParentIssueLabels()
    {
        // issue_comment events allow through only if the PARENT issue's
        // labels pass — BuildCommentPayload surfaces issue.labels for that.
        var config = NewConfig(includeLabels: new[] { "spring-voyage" });
        var payload = CommentPayloadWithIssueLabels(
            commentAuthor: "carol",
            issueAuthor: "alice",
            issueLabels: new[] { "spring-voyage" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_IncludeLabel_IssueCommentShape_ParentIssueMissingLabel_Drops()
    {
        var config = NewConfig(includeLabels: new[] { "spring-voyage" });
        var payload = CommentPayloadWithIssueLabels(
            commentAuthor: "carol",
            issueAuthor: "alice",
            issueLabels: new[] { "bug" });

        var result = GitHubEventFilter.Evaluate(config, payload);

        result.Allowed.ShouldBeFalse();
        result.Kind.ShouldBe("include_label");
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
            Repo: "acme/platform",
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

    private static JsonElement PullRequestPayloadWithLabels(
        string author,
        IReadOnlyList<string> labels)
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
                labels,
            },
        };
        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement CommentPayloadWithIssueLabels(
        string commentAuthor,
        string issueAuthor,
        IReadOnlyList<string> issueLabels)
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
                labels = issueLabels,
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
