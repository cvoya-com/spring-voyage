// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using System.Text.Json;
using Cvoya.Spring.Core.Messaging;
using Microsoft.Extensions.Logging;

/// <summary>
/// Processes incoming GitHub webhook payloads and translates them into
/// domain <see cref="Message"/> objects for the Spring Voyage platform.
/// </summary>
public class GitHubWebhookHandler(ILoggerFactory loggerFactory)
{
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

        return action switch
        {
            "opened" => CreateMessage(payload, "issue.opened", BuildIssuePayload(payload, "work_assignment")),
            "labeled" => CreateMessage(payload, "issue.labeled", BuildIssuePayload(payload, "label_change")),
            "assigned" => CreateMessage(payload, "issue.assigned", BuildIssuePayload(payload, "assignment")),
            _ => null
        };
    }

    private Message? TranslatePullRequestEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();

        return action switch
        {
            "opened" => CreateMessage(payload, "pull_request.opened", BuildPullRequestPayload(payload, "review_request")),
            "review_submitted" => CreateMessage(payload, "pull_request.review_submitted", BuildPullRequestPayload(payload, "review_result")),
            _ => null
        };
    }

    private Message? TranslateIssueCommentEvent(JsonElement payload)
    {
        var action = payload.GetProperty("action").GetString();

        return action switch
        {
            "created" => CreateMessage(payload, "issue_comment.created", BuildCommentPayload(payload)),
            _ => null
        };
    }

    private Message CreateMessage(JsonElement webhookPayload, string eventName, JsonElement domainPayload)
    {
        var repo = webhookPayload.GetProperty("repository");
        var repoFullName = repo.GetProperty("full_name").GetString() ?? "unknown";

        _logger.LogInformation(
            "Translating GitHub event {EventName} from {Repository}",
            eventName,
            repoFullName);

        return new Message(
            Id: Guid.NewGuid(),
            From: ConnectorAddress,
            To: new Address("system", "router"),
            Type: MessageType.Domain,
            ConversationId: null,
            Payload: domainPayload,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static JsonElement BuildIssuePayload(JsonElement payload, string intent)
    {
        var issue = payload.GetProperty("issue");
        var repo = payload.GetProperty("repository");

        var data = new
        {
            source = "github",
            intent,
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
                labels = ExtractLabels(issue),
                assignee = issue.TryGetProperty("assignee", out var assignee) && assignee.ValueKind != JsonValueKind.Null
                    ? assignee.GetProperty("login").GetString()
                    : null
            }
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildPullRequestPayload(JsonElement payload, string intent)
    {
        var pr = payload.GetProperty("pull_request");
        var repo = payload.GetProperty("repository");

        var data = new
        {
            source = "github",
            intent,
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
                head = pr.GetProperty("head").GetProperty("ref").GetString(),
                @base = pr.GetProperty("base").GetProperty("ref").GetString()
            }
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement BuildCommentPayload(JsonElement payload)
    {
        var comment = payload.GetProperty("comment");
        var issue = payload.GetProperty("issue");
        var repo = payload.GetProperty("repository");

        var data = new
        {
            source = "github",
            intent = "feedback",
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
}
