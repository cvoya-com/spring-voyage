// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Text.Json;

/// <summary>
/// Canonical webhook-to-intent vocabulary emitted by the GitHub connector
/// (#2676). Single source of truth: <see cref="Webhooks.GitHubWebhookHandler"/>
/// dispatches through <see cref="MapAction"/>, and the
/// <c>github.describe_inbound_contract</c> tool on
/// <see cref="GitHubSkillRegistry"/> publishes the same list to bound
/// agents so the OSS unit YAML no longer has to re-paste it.
/// </summary>
internal static class GitHubIntentVocabulary
{
    /// <summary>
    /// One entry per top-level inbound-message field. Drives the
    /// <c>envelope.fields</c> section of <c>github.describe_inbound_contract</c>.
    /// </summary>
    public static readonly IReadOnlyList<EnvelopeField> EnvelopeFields = new EnvelopeField[]
    {
        new("source",
            "Always the string 'github' for messages produced by this connector. Switch on this first so unrelated traffic (replies from other agents, non-GitHub connectors) does not enter the GitHub-event path."),
        new("intent",
            "Canonical token from the intent vocabulary published in this contract. Switch on this rather than on 'action' so a single arm covers every webhook variant that maps to the same intent."),
        new("action",
            "The raw GitHub webhook action (e.g. 'opened', 'labeled', 'review_submitted'). Carried through for callers that need the original signal."),
        new("repository",
            "Object: { owner, name, full_name }. The bound repository the event fired against."),
        new("issue",
            "Object describing the issue. Present on issue-anchored intents (work_assignment, label_change, assignment, edit, lifecycle) and on issue_comment events delivered under intent=feedback."),
        new("pull_request",
            "Object describing the pull request. Present on PR-anchored intents (review_request, review_result, code_change, lifecycle, edit) and on PR review-comment / review-thread events delivered under intent=feedback or intent=review_thread."),
        new("comment",
            "Object describing the comment body. Present on intent=feedback (both issue_comment.* and pull_request_review_comment.*)."),
        new("review",
            "Object describing the PR review. Present on intent=review_result."),
        new("changed_label",
            "String: the label that was added or removed. Present on intent=label_change."),
        new("changed_assignee",
            "String: the GitHub login that was assigned or unassigned. Present on intent=assignment."),
        new("state_transition",
            "Object: a derived label state-transition produced by the connector's label state machine. Present on intent=label_change when the underlying label change implies a state move."),
    };

    /// <summary>
    /// Canonical intent vocabulary. The order is significant only for
    /// rendering — runtime consumers should switch on
    /// <see cref="Intent.Token"/>, not position.
    /// </summary>
    public static readonly IReadOnlyList<Intent> All = new Intent[]
    {
        new("work_assignment",
            "A freshly opened issue. Treat as new work to triage or implement.",
            new[] { "issues.opened" }),
        new("label_change",
            "A label was added to or removed from an issue. 'changed_label' names the label; 'state_transition' carries any derived state move from the label state machine.",
            new[] { "issues.labeled", "issues.unlabeled" }),
        new("assignment",
            "An assignee was added to or removed from an issue. 'changed_assignee' names the GitHub login.",
            new[] { "issues.assigned", "issues.unassigned" }),
        new("edit",
            "An issue or pull-request title / body was edited.",
            new[] { "issues.edited", "pull_request.edited" }),
        new("lifecycle",
            "Issue closed or reopened, or pull request closed / converted to draft. Informational on a thread you may already own.",
            new[] { "issues.closed", "issues.reopened", "pull_request.closed", "pull_request.converted_to_draft" }),
        new("review_request",
            "A pull request was opened or marked ready for review.",
            new[] { "pull_request.opened", "pull_request.ready_for_review" }),
        new("review_result",
            "A pull-request review was submitted, edited, or dismissed.",
            new[] { "pull_request.review_submitted", "pull_request_review.submitted", "pull_request_review.edited", "pull_request_review.dismissed" }),
        new("code_change",
            "New commits were pushed to a pull request.",
            new[] { "pull_request.synchronize" }),
        new("feedback",
            "A comment was created, edited, or deleted on an issue or as a PR review comment. The 'comment' field carries the body.",
            new[] { "issue_comment.created", "issue_comment.edited", "issue_comment.deleted", "pull_request_review_comment.created", "pull_request_review_comment.edited", "pull_request_review_comment.deleted" }),
        new("review_thread",
            "A pull-request review thread was resolved or unresolved.",
            new[] { "pull_request_review_thread.resolved", "pull_request_review_thread.unresolved" }),
        new("installation_lifecycle",
            "GitHub App installation lifecycle event (operator-level). Most member agents acknowledge and drop these.",
            new[] { "installation.created", "installation.deleted", "installation.suspend", "installation.unsuspend" }),
        new("installation_repositories",
            "Repositories added to or removed from a GitHub App installation (operator-level).",
            new[] { "installation_repositories.added", "installation_repositories.removed" }),
        new("project_lifecycle",
            "A GitHub Projects v2 project was created, edited, closed, reopened, or deleted (operator-level).",
            new[] { "projects_v2.created", "projects_v2.edited", "projects_v2.closed", "projects_v2.reopened", "projects_v2.deleted" }),
        new("project_item_lifecycle",
            "A Projects v2 item was created, archived, restored, deleted, or converted (operator-level).",
            new[] { "projects_v2_item.created", "projects_v2_item.archived", "projects_v2_item.restored", "projects_v2_item.deleted", "projects_v2_item.converted" }),
        new("project_item_change",
            "A Projects v2 item was edited or reordered (operator-level).",
            new[] { "projects_v2_item.edited", "projects_v2_item.reordered" }),
    };

    /// <summary>
    /// Maps a (webhook event, action) pair to the canonical intent token,
    /// or returns null when the pair is not enumerated. The lookup is
    /// O(intents × actions-per-intent) — small enough that the linear walk
    /// is cheaper than a dictionary build at every dispatch.
    /// </summary>
    public static string? MapAction(string @event, string? action)
    {
        if (string.IsNullOrEmpty(@event) || string.IsNullOrEmpty(action))
        {
            return null;
        }

        var lookup = $"{@event}.{action}";
        foreach (var intent in All)
        {
            foreach (var src in intent.GithubActions)
            {
                if (string.Equals(src, lookup, StringComparison.Ordinal))
                {
                    return intent.Token;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Builds the JSON document returned by
    /// <c>github.describe_inbound_contract</c>. Static — the document
    /// depends only on the vocabulary above, not on any per-call state.
    /// </summary>
    public static JsonElement BuildContractDocument()
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("envelope");
            writer.WriteStartObject();
            writer.WritePropertyName("fields");
            writer.WriteStartArray();
            foreach (var field in EnvelopeFields)
            {
                writer.WriteStartObject();
                writer.WriteString("name", field.Name);
                writer.WriteString("description", field.Description);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("intents");
            writer.WriteStartArray();
            foreach (var intent in All)
            {
                writer.WriteStartObject();
                writer.WriteString("token", intent.Token);
                writer.WriteString("description", intent.Description);
                writer.WritePropertyName("github_actions");
                writer.WriteStartArray();
                foreach (var src in intent.GithubActions)
                {
                    writer.WriteStringValue(src);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public sealed record EnvelopeField(string Name, string Description);
    public sealed record Intent(string Token, string Description, IReadOnlyList<string> GithubActions);
}
