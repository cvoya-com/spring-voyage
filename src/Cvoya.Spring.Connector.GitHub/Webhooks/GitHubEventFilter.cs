// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using System.Text.Json;

/// <summary>
/// Pure evaluator for per-binding inbound webhook filters declared on
/// <see cref="UnitGitHubConfig"/>. Issue #2407.
/// </summary>
/// <remarks>
/// Filter semantics:
/// <list type="bullet">
///   <item><description><b>Conjunctive across kinds:</b> label AND author AND path.</description></item>
///   <item><description><b>Disjunctive within a kind:</b> label IN [a, b].</description></item>
///   <item><description><c>ExcludeLabels</c> short-circuits to a drop and is evaluated first.</description></item>
///   <item><description><c>IncludePaths</c> only applies to PR-shape events (events that
///   carry a <c>pull_request</c> object). Pure issue events ignore the path filter
///   even when it's set — they have no changed-files surface.</description></item>
///   <item><description>Null or empty for a kind means "no filter on that kind."</description></item>
/// </list>
/// The evaluator works directly off the translated domain payload produced
/// by <see cref="GitHubWebhookHandler"/>, NOT the raw webhook body, so the
/// schema stays decoupled from GitHub's wire shape — adding a new event
/// type only requires updating <see cref="GitHubWebhookHandler"/>'s
/// translator, not this filter.
/// </remarks>
public static class GitHubEventFilter
{
    /// <summary>
    /// Evaluates the four filter kinds against <paramref name="domainPayload"/>
    /// (the result of <see cref="GitHubWebhookHandler.TranslateEvent"/>).
    /// Returns <see cref="GitHubEventFilterResult.Allow"/> when the event
    /// passes (or when no filters are configured), and a drop result
    /// carrying the matched kind + value(s) when any filter rejects.
    /// </summary>
    /// <param name="config">The per-unit config; pulled from the binding store.</param>
    /// <param name="domainPayload">The translated payload produced by <see cref="GitHubWebhookHandler"/>.</param>
    public static GitHubEventFilterResult Evaluate(
        UnitGitHubConfig config,
        JsonElement domainPayload)
    {
        ArgumentNullException.ThrowIfNull(config);

        var labels = ExtractLabels(domainPayload);
        var author = ExtractAuthor(domainPayload);
        var (isPrShape, paths) = ExtractPaths(domainPayload);

        // 1. ExcludeLabels — short-circuit drop.
        if (HasValues(config.ExcludeLabels))
        {
            foreach (var excluded in config.ExcludeLabels!)
            {
                if (labels.Contains(excluded, StringComparer.OrdinalIgnoreCase))
                {
                    return GitHubEventFilterResult.Drop("exclude_label", excluded);
                }
            }
        }

        // 2. IncludeLabels — at least one label must match.
        if (HasValues(config.IncludeLabels))
        {
            if (!config.IncludeLabels!.Any(l => labels.Contains(l, StringComparer.OrdinalIgnoreCase)))
            {
                return GitHubEventFilterResult.Drop(
                    "include_label",
                    string.Join(",", config.IncludeLabels!));
            }
        }

        // 3. IncludeAuthors — author must be one of the listed logins.
        if (HasValues(config.IncludeAuthors))
        {
            if (string.IsNullOrEmpty(author)
                || !config.IncludeAuthors!.Any(a => string.Equals(a, author, StringComparison.OrdinalIgnoreCase)))
            {
                return GitHubEventFilterResult.Drop(
                    "include_author",
                    string.Join(",", config.IncludeAuthors!));
            }
        }

        // 4. IncludePaths — only applies to PR-shape events. Pure issue
        //    events have no changed-files surface; the filter is silently
        //    ignored rather than dropping every issue when paths are set.
        //    This mirrors how GitHub's own webhook delivery treats event
        //    shapes — operators set a path filter intending it to gate PRs,
        //    not to silence issues entirely.
        if (isPrShape && HasValues(config.IncludePaths))
        {
            if (paths.Count == 0
                || !paths.Any(p => config.IncludePaths!.Any(prefix =>
                    p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
            {
                return GitHubEventFilterResult.Drop(
                    "include_path",
                    string.Join(",", config.IncludePaths!));
            }
        }

        return GitHubEventFilterResult.Allow;
    }

    private static bool HasValues(IReadOnlyList<string>? list) => list is { Count: > 0 };

    private static IReadOnlyList<string> ExtractLabels(JsonElement domainPayload)
    {
        // Issue events surface labels under "issue.labels"; PR events
        // surface them under "pull_request.labels" — but the translated
        // PR payload doesn't carry labels today. Issue webhooks DO carry
        // them, so the include/exclude filter is effective for the
        // issue-event family; PR-shape labels can be added by extending
        // BuildPullRequestPayload (out of scope for #2407).
        if (TryGetObject(domainPayload, "issue", out var issue)
            && issue.TryGetProperty("labels", out var labels)
            && labels.ValueKind == JsonValueKind.Array)
        {
            return labels.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToList();
        }
        return Array.Empty<string>();
    }

    private static string? ExtractAuthor(JsonElement domainPayload)
    {
        // Author resolution by event shape:
        //   * issue events           — issue.author
        //   * pull_request events    — pull_request.author
        //   * issue_comment events   — comment.author (the speaker, not the
        //                              original issue/PR author — operators
        //                              filtering by author typically want
        //                              "who triggered this event")
        //   * review / review_comment / review_thread events — reviewer
        //   * other events           — fall back to the first author-shaped
        //                              field present
        if (TryGetObject(domainPayload, "comment", out var comment)
            && TryGetString(comment, "author", out var commentAuthor))
        {
            return commentAuthor;
        }
        if (TryGetObject(domainPayload, "review", out var review)
            && TryGetString(review, "reviewer", out var reviewer))
        {
            return reviewer;
        }
        if (TryGetObject(domainPayload, "pull_request", out var pr)
            && TryGetString(pr, "author", out var prAuthor))
        {
            return prAuthor;
        }
        if (TryGetObject(domainPayload, "issue", out var issue)
            && TryGetString(issue, "author", out var issueAuthor))
        {
            return issueAuthor;
        }
        return null;
    }

    private static (bool IsPrShape, IReadOnlyList<string> Paths) ExtractPaths(JsonElement domainPayload)
    {
        // A PR-shape event is any event whose translated payload carries a
        // "pull_request" object — covers pull_request, pull_request_review,
        // pull_request_review_comment, pull_request_review_thread. Issue
        // events do NOT carry pull_request and are ignored by the path
        // filter (see issue #2407 — IncludePaths only applies to PR-shape
        // events).
        //
        // The "files" array is populated lazily by
        // GitHubWebhookHandler.ApplyInboundFilterAsync — it fetches via
        // GET /pulls/{n}/files only when the unit binding actually
        // configures IncludePaths, so PR webhooks without a path filter
        // pay no extra round-trip. Review-comment events keep their own
        // comment.path so they remain effective even without the fetch.
        if (!TryGetObject(domainPayload, "pull_request", out _))
        {
            return (false, Array.Empty<string>());
        }

        // PR-shape: we may have a "files" array on the translated payload
        // (lazily hydrated by the handler when IncludePaths is set) or, on
        // review-comment events, a single "comment.path". Probe both — the
        // filter is disjunctive within the kind, so a hit on either source
        // counts.
        var paths = new List<string>();

        if (domainPayload.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                if (f.ValueKind == JsonValueKind.String)
                {
                    var s = f.GetString();
                    if (!string.IsNullOrEmpty(s))
                    {
                        paths.Add(s);
                    }
                }
                else if (f.ValueKind == JsonValueKind.Object && TryGetString(f, "path", out var p))
                {
                    paths.Add(p);
                }
            }
        }

        if (TryGetObject(domainPayload, "comment", out var c)
            && TryGetString(c, "path", out var cp))
        {
            paths.Add(cp);
        }

        return (true, paths);
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement parent, string name, out string value)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            if (!string.IsNullOrEmpty(s))
            {
                value = s;
                return true;
            }
        }
        value = string.Empty;
        return false;
    }
}

/// <summary>
/// Outcome of <see cref="GitHubEventFilter.Evaluate"/>. <see cref="Allow"/>
/// signals that the event passes every configured filter (or no filters are
/// configured); the drop variants carry the kind + matched value(s) so
/// callers can emit a structured audit signal.
/// </summary>
public readonly record struct GitHubEventFilterResult
{
    /// <summary>The shared singleton allow result.</summary>
    public static readonly GitHubEventFilterResult Allow = new(allowed: true, kind: null, value: null);

    /// <summary>True when the event passes every configured filter.</summary>
    public bool Allowed { get; }

    /// <summary>
    /// The filter kind that matched on drop — one of <c>exclude_label</c>,
    /// <c>include_label</c>, <c>include_author</c>, <c>include_path</c>.
    /// <c>null</c> when <see cref="Allowed"/> is true.
    /// </summary>
    public string? Kind { get; }

    /// <summary>
    /// The filter value(s) that drove the drop. For ExcludeLabels this is
    /// the single label that matched. For the include variants it's the
    /// comma-joined include list so operators can see which set the event
    /// failed against. <c>null</c> when <see cref="Allowed"/> is true.
    /// </summary>
    public string? Value { get; }

    private GitHubEventFilterResult(bool allowed, string? kind, string? value)
    {
        Allowed = allowed;
        Kind = kind;
        Value = value;
    }

    /// <summary>
    /// Constructs a drop result.
    /// </summary>
    /// <param name="kind">The filter kind that matched.</param>
    /// <param name="value">The matched value or join of the include set.</param>
    public static GitHubEventFilterResult Drop(string kind, string value) =>
        new(allowed: false, kind: kind, value: value);
}
