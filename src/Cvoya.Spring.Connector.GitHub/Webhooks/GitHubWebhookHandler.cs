// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Caching;
using Cvoya.Spring.Connector.GitHub.Labels;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// Processes incoming GitHub webhook payloads and translates them into
/// domain <see cref="Message"/> objects for the Spring Voyage platform.
/// </summary>
public class GitHubWebhookHandler : IGitHubWebhookHandler
{
    private readonly GitHubConnectorOptions _options;
    private readonly LabelStateMachine _labelStateMachine;

    /// <summary>
    /// Initializes the handler. The <paramref name="labelStateMachine"/> is
    /// optional — when omitted (e.g. in legacy test setups) label-change events
    /// are still translated but carry no derived <c>state_transition</c>.
    /// </summary>
    public GitHubWebhookHandler(
        GitHubConnectorOptions options,
        ILoggerFactory loggerFactory,
        LabelStateMachine? labelStateMachine = null)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<GitHubWebhookHandler>();
        _labelStateMachine = labelStateMachine ?? new LabelStateMachine(LabelStateMachineOptions.Default());
    }


    /// <summary>
    /// Fallback destination used when no target unit is configured. <see cref="IMessageRouter"/>
    /// does not recognize this scheme, so routing will fail with <c>ADDRESS_NOT_FOUND</c>
    /// — callers log and ack but no delivery occurs. Configure
    /// <see cref="GitHubConnectorOptions.DefaultTargetUnitPath"/> to route to a real unit.
    /// </summary>
    internal static readonly Address FallbackRouterAddress = new("system", "router");

    private static readonly Address ConnectorAddress = new("connector", "github");

    private readonly ILogger _logger;

    /// <summary>
    /// Translates a GitHub webhook event into a domain message.
    /// </summary>
    /// <param name="eventType">The GitHub event type from the X-GitHub-Event header.</param>
    /// <param name="payload">The parsed JSON payload.</param>
    /// <returns>A domain <see cref="Message"/>, or <c>null</c> if the event type is not handled.</returns>
    public Message? TranslateEvent(string eventType, JsonElement payload)
    {
        return eventType switch
        {
            "issues" => TranslateIssueEvent(payload),
            "pull_request" => TranslatePullRequestEvent(payload),
            "issue_comment" => TranslateIssueCommentEvent(payload),
            "pull_request_review" => TranslatePullRequestReviewEvent(payload),
            "pull_request_review_comment" => TranslatePullRequestReviewCommentEvent(payload),
            "pull_request_review_thread" => TranslatePullRequestReviewThreadEvent(payload),
            "installation" => TranslateInstallationEvent(payload),
            "installation_repositories" => TranslateInstallationRepositoriesEvent(payload),
            "projects_v2" => TranslateProjectsV2Event(payload),
            "projects_v2_item" => TranslateProjectsV2ItemEvent(payload),
            _ => null
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<string> DeriveInvalidationTags(string eventType, JsonElement payload)
    {
        // Projects v2 events live at the organization (or user) level, not
        // inside a repository — they carry "organization.login" instead of
        // "repository". Branch early so the repository-gated path below
        // stays straightforward for the repo-scoped events.
        if (eventType is "projects_v2" or "projects_v2_item")
        {
            return TagsForProjectsV2(eventType, payload);
        }

        // Every event that carries a repository + (issue | pull_request)
        // yields at least the per-resource tag; events on PRs also feed the
        // PR tag. The repo-wide tag is left out here because PR-specific
        // events rarely invalidate the entire repo list — callers that need
        // that behaviour can flush the repo tag manually.
        if (!payload.TryGetProperty("repository", out var repo) || repo.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        string? ownerLogin = null;
        if (repo.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object
            && owner.TryGetProperty("login", out var ownerLoginEl) && ownerLoginEl.ValueKind == JsonValueKind.String)
        {
            ownerLogin = ownerLoginEl.GetString();
        }
        string? repoName = null;
        if (repo.TryGetProperty("name", out var repoNameEl) && repoNameEl.ValueKind == JsonValueKind.String)
        {
            repoName = repoNameEl.GetString();
        }

        if (string.IsNullOrEmpty(ownerLogin) || string.IsNullOrEmpty(repoName))
        {
            return [];
        }

        return eventType switch
        {
            "issues" => TagsForIssue(ownerLogin, repoName, payload),
            // issue_comment covers PR comments too (GitHub dispatches both
            // issue and PR conversation comments through this event), so we
            // emit both Issue and PR tags so cached reads of either shape
            // are flushed in one pass.
            "issue_comment" => TagsForIssueComment(ownerLogin, repoName, payload),
            "pull_request" => TagsForPullRequest(ownerLogin, repoName, payload),
            "pull_request_review" => TagsForPullRequest(ownerLogin, repoName, payload),
            "pull_request_review_comment" => TagsForPullRequest(ownerLogin, repoName, payload),
            "pull_request_review_thread" => TagsForPullRequest(ownerLogin, repoName, payload),
            _ => [],
        };
    }

    private static IReadOnlyList<string> TagsForProjectsV2(string eventType, JsonElement payload)
    {
        // Both projects_v2.* and projects_v2_item.* include "organization"
        // at the top level — user-owned boards would carry "user" instead,
        // but that surface is not yet wired through TranslateEvent either.
        string? ownerLogin = null;
        if (payload.TryGetProperty("organization", out var org) && org.ValueKind == JsonValueKind.Object
            && org.TryGetProperty("login", out var orgLogin) && orgLogin.ValueKind == JsonValueKind.String)
        {
            ownerLogin = orgLogin.GetString();
        }

        if (eventType == "projects_v2")
        {
            // project number is carried on the "projects_v2" object. Without
            // it we can still flush the owner-wide list tag (a new board was
            // created / renamed) but not the per-project entry.
            if (string.IsNullOrEmpty(ownerLogin))
            {
                return [];
            }

            if (!payload.TryGetProperty("projects_v2", out var project) || project.ValueKind != JsonValueKind.Object
                || !project.TryGetProperty("number", out var numEl) || numEl.ValueKind != JsonValueKind.Number)
            {
                return [CacheTags.ProjectV2List(ownerLogin)];
            }

            var number = numEl.GetInt32();
            return
            [
                CacheTags.ProjectV2(ownerLogin, number),
                // Owner-wide list also becomes stale — the board's title,
                // lifecycle state, or existence may have changed.
                CacheTags.ProjectV2List(ownerLogin),
            ];
        }

        // projects_v2_item: item node id is what the skill caches under.
        // We deliberately do NOT emit the parent project tag here — the
        // webhook payload does not carry the board number, only the
        // project_node_id, and the tag scheme is keyed on owner/number. A
        // follow-up can bridge node-id → number via the GraphQL surface if
        // list pages need per-item invalidation.
        if (!payload.TryGetProperty("projects_v2_item", out var item) || item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("node_id", out var nodeIdEl) || nodeIdEl.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        var itemId = nodeIdEl.GetString();
        if (string.IsNullOrEmpty(itemId))
        {
            return [];
        }
        return [CacheTags.ProjectV2Item(itemId)];
    }

    private static IReadOnlyList<string> TagsForIssue(string owner, string repo, JsonElement payload)
    {
        if (!payload.TryGetProperty("issue", out var issue) || issue.ValueKind != JsonValueKind.Object
            || !issue.TryGetProperty("number", out var numberEl) || numberEl.ValueKind != JsonValueKind.Number)
        {
            return [];
        }
        return [CacheTags.Issue(owner, repo, numberEl.GetInt32())];
    }

    private static IReadOnlyList<string> TagsForIssueComment(string owner, string repo, JsonElement payload)
    {
        if (!payload.TryGetProperty("issue", out var issue) || issue.ValueKind != JsonValueKind.Object
            || !issue.TryGetProperty("number", out var numberEl) || numberEl.ValueKind != JsonValueKind.Number)
        {
            return [];
        }
        var number = numberEl.GetInt32();
        // Emit BOTH Issue and PR tags — this event is ambiguous between the
        // two and we'd rather over-invalidate than serve stale comments.
        return
        [
            CacheTags.Issue(owner, repo, number),
            CacheTags.PullRequest(owner, repo, number),
        ];
    }

    private static IReadOnlyList<string> TagsForPullRequest(string owner, string repo, JsonElement payload)
    {
        if (!payload.TryGetProperty("pull_request", out var pr) || pr.ValueKind != JsonValueKind.Object
            || !pr.TryGetProperty("number", out var numberEl) || numberEl.ValueKind != JsonValueKind.Number)
        {
            return [];
        }
        var number = numberEl.GetInt32();
        return
        [
            CacheTags.PullRequest(owner, repo, number),
            // Cross-cut: PR cache reads keyed under the issue tag (e.g.
            // comments on a PR) must also be flushed when the PR itself
            // changes, so emit Issue(number) here too.
            CacheTags.Issue(owner, repo, number),
        ];
    }

    private Message? TranslateIssueEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();

        // Intent vocabulary aligns with v1's coordinator dispatch so downstream
        // units can switch on a single string rather than (event, action) pairs.
        return action switch
        {
            "opened" => CreateMessage(payload, "issue.opened", BuildIssuePayload(payload, "work_assignment", action)),
            "labeled" => CreateMessage(payload, "issue.labeled", BuildIssuePayload(payload, "label_change", action)),
            "unlabeled" => CreateMessage(payload, "issue.unlabeled", BuildIssuePayload(payload, "label_change", action)),
            "assigned" => CreateMessage(payload, "issue.assigned", BuildIssuePayload(payload, "assignment", action)),
            "unassigned" => CreateMessage(payload, "issue.unassigned", BuildIssuePayload(payload, "assignment", action)),
            "edited" => CreateMessage(payload, "issue.edited", BuildIssuePayload(payload, "edit", action)),
            "closed" => CreateMessage(payload, "issue.closed", BuildIssuePayload(payload, "lifecycle", action)),
            "reopened" => CreateMessage(payload, "issue.reopened", BuildIssuePayload(payload, "lifecycle", action)),
            _ => null
        };
    }

    private Message? TranslatePullRequestEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();

        return action switch
        {
            "opened" => CreateMessage(payload, "pull_request.opened", BuildPullRequestPayload(payload, "review_request", action)),
            "review_submitted" => CreateMessage(payload, "pull_request.review_submitted", BuildPullRequestPayload(payload, "review_result", action)),
            "synchronize" => CreateMessage(payload, "pull_request.synchronize", BuildPullRequestPayload(payload, "code_change", action)),
            "ready_for_review" => CreateMessage(payload, "pull_request.ready_for_review", BuildPullRequestPayload(payload, "review_request", action)),
            "converted_to_draft" => CreateMessage(payload, "pull_request.converted_to_draft", BuildPullRequestPayload(payload, "lifecycle", action)),
            "closed" => CreateMessage(payload, "pull_request.closed", BuildPullRequestPayload(payload, "lifecycle", action)),
            "edited" => CreateMessage(payload, "pull_request.edited", BuildPullRequestPayload(payload, "edit", action)),
            _ => null
        };
    }

    private Message? TranslateIssueCommentEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();

        return action switch
        {
            "created" => CreateMessage(payload, "issue_comment.created", BuildCommentPayload(payload, "feedback", action)),
            "edited" => CreateMessage(payload, "issue_comment.edited", BuildCommentPayload(payload, "feedback", action)),
            "deleted" => CreateMessage(payload, "issue_comment.deleted", BuildCommentPayload(payload, "feedback", action)),
            _ => null
        };
    }

    private Message CreateMessage(JsonElement webhookPayload, string eventName, JsonElement domainPayload)
    {
        // Some events (installation, installation_repositories) do not carry a repository field.
        // Fall back to the first added/impacted repository full name, or "unknown" when unavailable.
        string repoFullName = "unknown";
        if (webhookPayload.TryGetProperty("repository", out var repo)
            && repo.ValueKind == JsonValueKind.Object
            && repo.TryGetProperty("full_name", out var fn)
            && fn.ValueKind == JsonValueKind.String)
        {
            repoFullName = fn.GetString() ?? "unknown";
        }

        var destination = ResolveDestination(repoFullName);

        _logger.LogInformation(
            "Translating GitHub event {EventName} from {Repository} to {Scheme}://{Path}",
            eventName,
            repoFullName,
            destination.Scheme,
            destination.Path);

        return new Message(
            Id: Guid.NewGuid(),
            From: ConnectorAddress,
            To: destination,
            Type: MessageType.Domain,
            ThreadId: null,
            Payload: domainPayload,
            Timestamp: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Determines the routing destination for a translated webhook message.
    /// Until a per-installation unit lookup lands (see issue #109), we use the
    /// single configured <see cref="GitHubConnectorOptions.DefaultTargetUnitPath"/>
    /// for every repository. When unset, fall back to the legacy
    /// <c>system://router</c> sentinel and warn — <see cref="IMessageRouter"/>
    /// will Failure-route and the endpoint will still acknowledge the webhook.
    /// </summary>
    private Address ResolveDestination(string repoFullName)
    {
        var unitPath = _options.DefaultTargetUnitPath;
        if (!string.IsNullOrWhiteSpace(unitPath))
        {
            return new Address("unit", unitPath);
        }

        _logger.LogWarning(
            "No DefaultTargetUnitPath configured for the GitHub connector; webhook from {Repository} "
            + "will be addressed to system://router which the message router does not recognize.",
            repoFullName);
        return FallbackRouterAddress;
    }

    private JsonElement BuildIssuePayload(JsonElement payload, string intent, string? action)
    {
        var issue = payload.GetProperty("issue");
        var repo = payload.GetProperty("repository");

        // Action-specific delta fields — populated only when the webhook carries them,
        // mirroring v1's coordinator payload shape so downstream consumers can read
        // a consistent structure regardless of which action fired.
        string? changedLabel = null;
        if (payload.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.Object)
        {
            changedLabel = label.GetProperty("name").GetString();
        }

        string? changedAssignee = null;
        if (payload.TryGetProperty("assignee", out var actionAssignee) && actionAssignee.ValueKind == JsonValueKind.Object)
        {
            changedAssignee = actionAssignee.GetProperty("login").GetString();
        }

        var labels = ExtractLabels(issue);

        // Derive a state_transition for labeled / unlabeled actions so downstream
        // agents can react without re-implementing the label state machine.
        LabelStateTransition? stateTransition = null;
        if (action is "labeled" or "unlabeled")
        {
            stateTransition = _labelStateMachine.Derive(labels, changedLabel, action);
        }

        var data = new
        {
            source = "github",
            intent,
            action,
            repository = new
            {
                owner = repo.GetProperty("owner").GetProperty("login").GetString(),
                name = repo.GetProperty("name").GetString(),
                full_name = repo.GetProperty("full_name").GetString()
            },
            issue = new
            {
                number = issue.GetProperty("number").GetInt32(),
                title = issue.GetProperty("title").GetString(),
                body = issue.TryGetProperty("body", out var body) ? body.GetString() : null,
                state = issue.TryGetProperty("state", out var state) ? state.GetString() : null,
                labels,
                assignee = issue.TryGetProperty("assignee", out var assignee) && assignee.ValueKind != JsonValueKind.Null
                    ? assignee.GetProperty("login").GetString()
                    : null,
                assignees = ExtractAssignees(issue),
                author = issue.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                    ? user.GetProperty("login").GetString()
                    : null,
            },
            changed_label = changedLabel,
            changed_assignee = changedAssignee,
            state_transition = stateTransition,
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildPullRequestPayload(JsonElement payload, string intent, string? action)
    {
        var pr = payload.GetProperty("pull_request");
        var repo = payload.GetProperty("repository");

        var merged = pr.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True;
        var draft = pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;

        var data = new
        {
            source = "github",
            intent,
            action,
            repository = new
            {
                owner = repo.GetProperty("owner").GetProperty("login").GetString(),
                name = repo.GetProperty("name").GetString(),
                full_name = repo.GetProperty("full_name").GetString()
            },
            pull_request = new
            {
                number = pr.GetProperty("number").GetInt32(),
                title = pr.GetProperty("title").GetString(),
                body = pr.TryGetProperty("body", out var body) ? body.GetString() : null,
                state = pr.TryGetProperty("state", out var state) ? state.GetString() : null,
                head = pr.GetProperty("head").GetProperty("ref").GetString(),
                @base = pr.GetProperty("base").GetProperty("ref").GetString(),
                draft,
                merged,
                author = pr.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                    ? user.GetProperty("login").GetString()
                    : null,
            }
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildCommentPayload(JsonElement payload, string intent, string? action)
    {
        var comment = payload.GetProperty("comment");
        var issue = payload.GetProperty("issue");
        var repo = payload.GetProperty("repository");

        var data = new
        {
            source = "github",
            intent,
            action,
            repository = new
            {
                owner = repo.GetProperty("owner").GetProperty("login").GetString(),
                name = repo.GetProperty("name").GetString(),
                full_name = repo.GetProperty("full_name").GetString()
            },
            issue = new
            {
                number = issue.GetProperty("number").GetInt32(),
                title = issue.GetProperty("title").GetString()
            },
            comment = new
            {
                id = comment.GetProperty("id").GetInt64(),
                body = comment.GetProperty("body").GetString(),
                author = comment.GetProperty("user").GetProperty("login").GetString()
            }
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private Message? TranslatePullRequestReviewEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        return action switch
        {
            "submitted" => CreateMessage(payload, "pull_request_review.submitted", BuildPullRequestReviewPayload(payload, "review_result", action)),
            "edited" => CreateMessage(payload, "pull_request_review.edited", BuildPullRequestReviewPayload(payload, "review_result", action)),
            "dismissed" => CreateMessage(payload, "pull_request_review.dismissed", BuildPullRequestReviewPayload(payload, "review_result", action)),
            _ => null,
        };
    }

    private Message? TranslatePullRequestReviewCommentEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        return action switch
        {
            "created" => CreateMessage(payload, "pull_request_review_comment.created", BuildPullRequestReviewCommentPayload(payload, "feedback", action)),
            "edited" => CreateMessage(payload, "pull_request_review_comment.edited", BuildPullRequestReviewCommentPayload(payload, "feedback", action)),
            "deleted" => CreateMessage(payload, "pull_request_review_comment.deleted", BuildPullRequestReviewCommentPayload(payload, "feedback", action)),
            _ => null,
        };
    }

    private Message? TranslatePullRequestReviewThreadEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        return action switch
        {
            "resolved" => CreateMessage(payload, "pull_request_review_thread.resolved", BuildPullRequestReviewThreadPayload(payload, "review_thread", action)),
            "unresolved" => CreateMessage(payload, "pull_request_review_thread.unresolved", BuildPullRequestReviewThreadPayload(payload, "review_thread", action)),
            _ => null,
        };
    }

    private Message? TranslateInstallationEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        return action switch
        {
            "created" => CreateMessage(payload, "installation.created", BuildInstallationPayload(payload, "installation_lifecycle", action)),
            "deleted" => CreateMessage(payload, "installation.deleted", BuildInstallationPayload(payload, "installation_lifecycle", action)),
            "suspend" => CreateMessage(payload, "installation.suspend", BuildInstallationPayload(payload, "installation_lifecycle", action)),
            "unsuspend" => CreateMessage(payload, "installation.unsuspend", BuildInstallationPayload(payload, "installation_lifecycle", action)),
            _ => null,
        };
    }

    private Message? TranslateInstallationRepositoriesEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        return action switch
        {
            "added" => CreateMessage(payload, "installation_repositories.added", BuildInstallationRepositoriesPayload(payload, "installation_repositories", action)),
            "removed" => CreateMessage(payload, "installation_repositories.removed", BuildInstallationRepositoriesPayload(payload, "installation_repositories", action)),
            _ => null,
        };
    }

    private static JsonElement BuildPullRequestReviewPayload(JsonElement payload, string intent, string? action)
    {
        var review = payload.GetProperty("review");
        var pr = payload.GetProperty("pull_request");
        var repo = payload.GetProperty("repository");

        var data = new
        {
            source = "github",
            intent,
            action,
            repository = new
            {
                owner = repo.GetProperty("owner").GetProperty("login").GetString(),
                name = repo.GetProperty("name").GetString(),
                full_name = repo.GetProperty("full_name").GetString(),
            },
            pull_request = new
            {
                number = pr.GetProperty("number").GetInt32(),
                title = pr.TryGetProperty("title", out var t) ? t.GetString() : null,
                author = pr.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                    ? u.GetProperty("login").GetString()
                    : null,
            },
            review = new
            {
                id = review.TryGetProperty("id", out var rid) ? rid.GetInt64() : 0L,
                state = review.TryGetProperty("state", out var rs) ? rs.GetString() : null,
                body = review.TryGetProperty("body", out var rb) ? rb.GetString() : null,
                reviewer = review.TryGetProperty("user", out var ru) && ru.ValueKind == JsonValueKind.Object
                    ? ru.GetProperty("login").GetString()
                    : null,
                submitted_at = review.TryGetProperty("submitted_at", out var sa) && sa.ValueKind == JsonValueKind.String
                    ? sa.GetString()
                    : null,
            },
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildPullRequestReviewCommentPayload(JsonElement payload, string intent, string? action)
    {
        var comment = payload.GetProperty("comment");
        var pr = payload.GetProperty("pull_request");
        var repo = payload.GetProperty("repository");

        var data = new
        {
            source = "github",
            intent,
            action,
            repository = new
            {
                owner = repo.GetProperty("owner").GetProperty("login").GetString(),
                name = repo.GetProperty("name").GetString(),
                full_name = repo.GetProperty("full_name").GetString(),
            },
            pull_request = new
            {
                number = pr.GetProperty("number").GetInt32(),
                title = pr.TryGetProperty("title", out var t) ? t.GetString() : null,
            },
            comment = new
            {
                id = comment.GetProperty("id").GetInt64(),
                body = comment.TryGetProperty("body", out var cb) ? cb.GetString() : null,
                path = comment.TryGetProperty("path", out var cp) ? cp.GetString() : null,
                position = comment.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Number
                    ? pos.GetInt32()
                    : (int?)null,
                diff_hunk = comment.TryGetProperty("diff_hunk", out var dh) ? dh.GetString() : null,
                commit_id = comment.TryGetProperty("commit_id", out var ci) ? ci.GetString() : null,
                in_reply_to_id = comment.TryGetProperty("in_reply_to_id", out var rt) && rt.ValueKind == JsonValueKind.Number
                    ? rt.GetInt64()
                    : (long?)null,
                author = comment.TryGetProperty("user", out var cu) && cu.ValueKind == JsonValueKind.Object
                    ? cu.GetProperty("login").GetString()
                    : null,
            },
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildPullRequestReviewThreadPayload(JsonElement payload, string intent, string? action)
    {
        var thread = payload.GetProperty("thread");
        var pr = payload.GetProperty("pull_request");
        var repo = payload.GetProperty("repository");

        long? threadId = null;
        if (thread.TryGetProperty("node_id", out _) && thread.TryGetProperty("id", out var tid) && tid.ValueKind == JsonValueKind.Number)
        {
            threadId = tid.GetInt64();
        }

        string? nodeId = thread.TryGetProperty("node_id", out var nid) ? nid.GetString() : null;

        var data = new
        {
            source = "github",
            intent,
            action,
            repository = new
            {
                owner = repo.GetProperty("owner").GetProperty("login").GetString(),
                name = repo.GetProperty("name").GetString(),
                full_name = repo.GetProperty("full_name").GetString(),
            },
            pull_request = new
            {
                number = pr.GetProperty("number").GetInt32(),
                title = pr.TryGetProperty("title", out var t) ? t.GetString() : null,
            },
            thread = new
            {
                id = threadId,
                node_id = nodeId,
                resolved = string.Equals(action, "resolved", StringComparison.Ordinal),
            },
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildInstallationPayload(JsonElement payload, string intent, string? action)
    {
        var installation = payload.GetProperty("installation");

        string? reason = null;
        if (payload.TryGetProperty("sender", out var sender)
            && sender.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("suspended_by", out var sb)
            && sb.ValueKind == JsonValueKind.Object)
        {
            reason = "suspended_by_" + sb.GetProperty("login").GetString();
        }
        // GitHub includes "suspended_at" / "suspended_by" on installation payloads
        // when the installation is suspended; surface both verbatim for consumers
        // who need to persist the reason.
        string? suspendedAt = installation.TryGetProperty("suspended_at", out var sa) && sa.ValueKind == JsonValueKind.String
            ? sa.GetString()
            : null;
        string? suspendedBy = installation.TryGetProperty("suspended_by", out var ssb) && ssb.ValueKind == JsonValueKind.Object
            ? ssb.GetProperty("login").GetString()
            : null;

        var data = new
        {
            source = "github",
            intent,
            action,
            installation = new
            {
                id = installation.GetProperty("id").GetInt64(),
                account = installation.TryGetProperty("account", out var acct) && acct.ValueKind == JsonValueKind.Object
                    ? acct.GetProperty("login").GetString()
                    : null,
                account_type = installation.TryGetProperty("account", out var acct2) && acct2.ValueKind == JsonValueKind.Object
                    && acct2.TryGetProperty("type", out var at) ? at.GetString() : null,
                repository_selection = installation.TryGetProperty("repository_selection", out var rsel)
                    ? rsel.GetString()
                    : null,
                suspended_at = suspendedAt,
                suspended_by = suspendedBy,
            },
            repositories = ExtractInstallationRepositories(payload, "repositories"),
            suspension_reason = reason,
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildInstallationRepositoriesPayload(JsonElement payload, string intent, string? action)
    {
        var installation = payload.GetProperty("installation");

        var data = new
        {
            source = "github",
            intent,
            action,
            installation = new
            {
                id = installation.GetProperty("id").GetInt64(),
                account = installation.TryGetProperty("account", out var acct) && acct.ValueKind == JsonValueKind.Object
                    ? acct.GetProperty("login").GetString()
                    : null,
                repository_selection = installation.TryGetProperty("repository_selection", out var rsel)
                    ? rsel.GetString()
                    : null,
            },
            added_repositories = ExtractInstallationRepositories(payload, "repositories_added"),
            removed_repositories = ExtractInstallationRepositories(payload, "repositories_removed"),
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private Message? TranslateProjectsV2Event(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        // Projects v2 events fire at the org level (organization:<login> hook scope).
        // We translate the common lifecycle actions; unknown actions fall through to
        // null so the endpoint still acks without manufacturing a synthetic message.
        return action switch
        {
            "created" => CreateMessage(payload, "projects_v2.created", BuildProjectsV2Payload(payload, "project_lifecycle", action)),
            "edited" => CreateMessage(payload, "projects_v2.edited", BuildProjectsV2Payload(payload, "project_lifecycle", action)),
            "closed" => CreateMessage(payload, "projects_v2.closed", BuildProjectsV2Payload(payload, "project_lifecycle", action)),
            "reopened" => CreateMessage(payload, "projects_v2.reopened", BuildProjectsV2Payload(payload, "project_lifecycle", action)),
            "deleted" => CreateMessage(payload, "projects_v2.deleted", BuildProjectsV2Payload(payload, "project_lifecycle", action)),
            _ => null,
        };
    }

    private Message? TranslateProjectsV2ItemEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();
        return action switch
        {
            "created" => CreateMessage(payload, "projects_v2_item.created", BuildProjectsV2ItemPayload(payload, "project_item_lifecycle", action)),
            "edited" => CreateMessage(payload, "projects_v2_item.edited", BuildProjectsV2ItemPayload(payload, "project_item_change", action)),
            "archived" => CreateMessage(payload, "projects_v2_item.archived", BuildProjectsV2ItemPayload(payload, "project_item_lifecycle", action)),
            "restored" => CreateMessage(payload, "projects_v2_item.restored", BuildProjectsV2ItemPayload(payload, "project_item_lifecycle", action)),
            "deleted" => CreateMessage(payload, "projects_v2_item.deleted", BuildProjectsV2ItemPayload(payload, "project_item_lifecycle", action)),
            "converted" => CreateMessage(payload, "projects_v2_item.converted", BuildProjectsV2ItemPayload(payload, "project_item_lifecycle", action)),
            "reordered" => CreateMessage(payload, "projects_v2_item.reordered", BuildProjectsV2ItemPayload(payload, "project_item_change", action)),
            _ => null,
        };
    }

    private static JsonElement BuildProjectsV2Payload(JsonElement payload, string intent, string? action)
    {
        // projects_v2 webhook shape: top-level "projects_v2" plus "organization" (and
        // "installation" when org-installed). There is no "repository" field.
        var project = payload.TryGetProperty("projects_v2", out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : (JsonElement?)null;
        var orgLogin = payload.TryGetProperty("organization", out var org) && org.ValueKind == JsonValueKind.Object
            && org.TryGetProperty("login", out var ol) && ol.ValueKind == JsonValueKind.String
            ? ol.GetString()
            : null;

        var data = new
        {
            source = "github",
            intent,
            action,
            owner = orgLogin,
            project = project is { } pe ? new
            {
                id = pe.TryGetProperty("node_id", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() : null,
                database_id = pe.TryGetProperty("id", out var did) && did.ValueKind == JsonValueKind.Number ? did.GetInt64() : 0L,
                number = pe.TryGetProperty("number", out var pn) && pn.ValueKind == JsonValueKind.Number ? pn.GetInt32() : 0,
                title = pe.TryGetProperty("title", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString() : null,
                closed = pe.TryGetProperty("closed", out var pc) && pc.ValueKind == JsonValueKind.True,
            } : null,
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildProjectsV2ItemPayload(JsonElement payload, string intent, string? action)
    {
        var item = payload.TryGetProperty("projects_v2_item", out var it) && it.ValueKind == JsonValueKind.Object
            ? it
            : (JsonElement?)null;
        var orgLogin = payload.TryGetProperty("organization", out var org) && org.ValueKind == JsonValueKind.Object
            && org.TryGetProperty("login", out var ol) && ol.ValueKind == JsonValueKind.String
            ? ol.GetString()
            : null;

        // field_value_changes fires only on "edited"; surface verbatim as JsonElement
        // so downstream consumers can inspect from/to without us re-encoding every shape.
        JsonElement? fieldChanges = null;
        if (payload.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object
            && changes.TryGetProperty("field_value", out var fv) && fv.ValueKind == JsonValueKind.Object)
        {
            fieldChanges = fv;
        }

        var data = new
        {
            source = "github",
            intent,
            action,
            owner = orgLogin,
            project_id = item is { } ie && ie.TryGetProperty("project_node_id", out var pid) && pid.ValueKind == JsonValueKind.String
                ? pid.GetString()
                : null,
            item = item is { } ie2 ? new
            {
                id = ie2.TryGetProperty("node_id", out var nid) && nid.ValueKind == JsonValueKind.String ? nid.GetString() : null,
                database_id = ie2.TryGetProperty("id", out var did) && did.ValueKind == JsonValueKind.Number ? did.GetInt64() : 0L,
                content_type = ie2.TryGetProperty("content_type", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString() : null,
                content_node_id = ie2.TryGetProperty("content_node_id", out var cnid) && cnid.ValueKind == JsonValueKind.String ? cnid.GetString() : null,
                archived = ie2.TryGetProperty("archived_at", out var ar) && ar.ValueKind == JsonValueKind.String,
            } : null,
            field_value_changes = fieldChanges,
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static object[] ExtractInstallationRepositories(JsonElement payload, string property)
    {
        if (!payload.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return arr.EnumerateArray()
            .Where(r => r.ValueKind == JsonValueKind.Object)
            .Select(r => (object)new
            {
                id = r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0L,
                name = r.TryGetProperty("name", out var n) ? n.GetString() : null,
                full_name = r.TryGetProperty("full_name", out var fn) ? fn.GetString() : null,
                @private = r.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True,
            })
            .ToArray();
    }

    private static string[] ExtractLabels(JsonElement issue)
    {
        if (!issue.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return labels.EnumerateArray()
            .Select(l => l.GetProperty("name").GetString() ?? string.Empty)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToArray();
    }

    private static string[] ExtractAssignees(JsonElement issue)
    {
        if (!issue.TryGetProperty("assignees", out var assignees) || assignees.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return assignees.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.Object)
            .Select(a => a.GetProperty("login").GetString() ?? string.Empty)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToArray();
    }
}