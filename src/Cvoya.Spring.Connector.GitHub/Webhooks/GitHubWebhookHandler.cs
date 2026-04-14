// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// Processes incoming GitHub webhook payloads and translates them into
/// domain <see cref="Message"/> objects for the Spring Voyage platform.
/// </summary>
public class GitHubWebhookHandler(
    GitHubConnectorOptions options,
    ILoggerFactory loggerFactory) : IGitHubWebhookHandler
{
    /// <summary>
    /// Fallback destination used when no target unit is configured. <see cref="IMessageRouter"/>
    /// does not recognize this scheme, so routing will fail with <c>ADDRESS_NOT_FOUND</c>
    /// — callers log and ack but no delivery occurs. Configure
    /// <see cref="GitHubConnectorOptions.DefaultTargetUnitPath"/> to route to a real unit.
    /// </summary>
    internal static readonly Address FallbackRouterAddress = new("system", "router");

    private static readonly Address ConnectorAddress = new("connector", "github");

    private readonly ILogger _logger = loggerFactory.CreateLogger<GitHubWebhookHandler>();

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
            _ => null
        };
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
        var repo = webhookPayload.GetProperty("repository");
        var repoFullName = repo.GetProperty("full_name").GetString() ?? "unknown";

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
            ConversationId: null,
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
        var unitPath = options.DefaultTargetUnitPath;
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

    private static JsonElement BuildIssuePayload(JsonElement payload, string intent, string? action)
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
                labels = ExtractLabels(issue),
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