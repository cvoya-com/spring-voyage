// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Core.Messaging;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Xunit;

public class GitHubWebhookHandlerTests
{
    private readonly GitHubWebhookHandler _handler;

    public GitHubWebhookHandlerTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        var logger = Substitute.For<ILogger>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        _handler = new GitHubWebhookHandler(loggerFactory);
    }

    [Fact]
    public void TranslateEvent_IssuesOpened_ReturnsDomainMessage()
    {
        var payload = CreateIssuePayload("opened");

        var message = _handler.TranslateEvent("issues", payload);

        message.Should().NotBeNull();
        message!.Type.Should().Be(MessageType.Domain);
        message.Payload.GetProperty("intent").GetString().Should().Be("work_assignment");
        message.Payload.GetProperty("issue").GetProperty("number").GetInt32().Should().Be(42);
    }

    [Fact]
    public void TranslateEvent_PullRequestOpened_ReturnsDomainMessage()
    {
        var payload = CreatePullRequestPayload("opened");

        var message = _handler.TranslateEvent("pull_request", payload);

        message.Should().NotBeNull();
        message!.Type.Should().Be(MessageType.Domain);
        message.Payload.GetProperty("intent").GetString().Should().Be("review_request");
        message.Payload.GetProperty("pull_request").GetProperty("number").GetInt32().Should().Be(10);
    }

    [Fact]
    public void TranslateEvent_IssueCommentCreated_ReturnsDomainMessage()
    {
        var payload = CreateCommentPayload();

        var message = _handler.TranslateEvent("issue_comment", payload);

        message.Should().NotBeNull();
        message!.Type.Should().Be(MessageType.Domain);
        message.Payload.GetProperty("intent").GetString().Should().Be("feedback");
        message.Payload.GetProperty("comment").GetProperty("body").GetString().Should().Be("Looks good!");
    }

    [Fact]
    public void TranslateEvent_UnknownEventType_ReturnsNull()
    {
        var payload = JsonSerializer.SerializeToElement(new { action = "opened" });

        var message = _handler.TranslateEvent("deployment", payload);

        message.Should().BeNull();
    }

    [Fact]
    public void TranslateEvent_Message_HasCorrectFromAddress()
    {
        var payload = CreateIssuePayload("opened");

        var message = _handler.TranslateEvent("issues", payload);

        message.Should().NotBeNull();
        message!.From.Scheme.Should().Be("connector");
        message.From.Path.Should().Be("github");
    }

    private static JsonElement CreateIssuePayload(string action)
    {
        var data = new
        {
            action,
            issue = new
            {
                number = 42,
                title = "Test issue",
                body = "Issue body",
                labels = new[] { new { name = "bug" } },
                assignee = new { login = "testuser" }
            },
            repository = new
            {
                name = "test-repo",
                full_name = "owner/test-repo",
                owner = new { login = "owner" }
            }
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement CreatePullRequestPayload(string action)
    {
        var data = new
        {
            action,
            pull_request = new
            {
                number = 10,
                title = "Test PR",
                body = "PR body",
                head = new { @ref = "feature-branch" },
                @base = new { @ref = "main" }
            },
            repository = new
            {
                name = "test-repo",
                full_name = "owner/test-repo",
                owner = new { login = "owner" }
            }
        };

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement CreateCommentPayload()
    {
        var data = new
        {
            action = "created",
            comment = new
            {
                id = 123L,
                body = "Looks good!",
                user = new { login = "reviewer" }
            },
            issue = new
            {
                number = 42,
                title = "Test issue"
            },
            repository = new
            {
                name = "test-repo",
                full_name = "owner/test-repo",
                owner = new { login = "owner" }
            }
        };

        return JsonSerializer.SerializeToElement(data);
    }
}