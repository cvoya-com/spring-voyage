// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Labels;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

public class GitHubWebhookHandlerTests
{
    private static readonly string TestTeamHex = TestSlugIds.HexFor("test-team");

    private readonly GitHubWebhookHandler _handler;

    public GitHubWebhookHandlerTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        var logger = Substitute.For<ILogger>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        var options = new GitHubConnectorOptions { DefaultTargetUnitPath = TestTeamHex };
        _handler = new GitHubWebhookHandler(options, loggerFactory);
    }

    [Fact]
    public void TranslateEvent_IssuesOpened_ReturnsDomainMessage()
    {
        var payload = CreateIssuePayload("opened");

        var message = _handler.TranslateEvent("issues", payload);

        message.ShouldNotBeNull();
        message!.Type.ShouldBe(MessageType.Domain);
        message.Payload.GetProperty("intent").GetString().ShouldBe("work_assignment");
        message.Payload.GetProperty("issue").GetProperty("number").GetInt32().ShouldBe(42);
    }

    [Fact]
    public void TranslateEvent_PullRequestOpened_ReturnsDomainMessage()
    {
        var payload = CreatePullRequestPayload("opened");

        var message = _handler.TranslateEvent("pull_request", payload);

        message.ShouldNotBeNull();
        message!.Type.ShouldBe(MessageType.Domain);
        message.Payload.GetProperty("intent").GetString().ShouldBe("review_request");
        message.Payload.GetProperty("pull_request").GetProperty("number").GetInt32().ShouldBe(10);
    }

    [Fact]
    public void TranslateEvent_IssueCommentCreated_ReturnsDomainMessage()
    {
        var payload = CreateCommentPayload();

        var message = _handler.TranslateEvent("issue_comment", payload);

        message.ShouldNotBeNull();
        message!.Type.ShouldBe(MessageType.Domain);
        message.Payload.GetProperty("intent").GetString().ShouldBe("feedback");
        message.Payload.GetProperty("comment").GetProperty("body").GetString().ShouldBe("Looks good!");
    }

    [Fact]
    public void TranslateEvent_UnknownEventType_ReturnsNull()
    {
        var payload = JsonSerializer.SerializeToElement(new { action = "opened" });

        var message = _handler.TranslateEvent("deployment", payload);

        message.ShouldBeNull();
    }

    [Theory]
    [InlineData("unlabeled", "label_change")]
    [InlineData("unassigned", "assignment")]
    [InlineData("edited", "edit")]
    [InlineData("closed", "lifecycle")]
    [InlineData("reopened", "lifecycle")]
    public void TranslateEvent_IssuesExpandedActions_ReturnsDomainMessage(string action, string expectedIntent)
    {
        var payload = CreateIssuePayload(action);

        var message = _handler.TranslateEvent("issues", payload);

        message.ShouldNotBeNull();
        message!.Type.ShouldBe(MessageType.Domain);
        message.Payload.GetProperty("intent").GetString().ShouldBe(expectedIntent);
        message.Payload.GetProperty("action").GetString().ShouldBe(action);
    }

    [Fact]
    public void TranslateEvent_IssuesUnknownAction_ReturnsNull()
    {
        var payload = CreateIssuePayload("milestoned");

        var message = _handler.TranslateEvent("issues", payload);

        message.ShouldBeNull();
    }

    [Fact]
    public void TranslateEvent_IssuesUnlabeled_IncludesChangedLabelName()
    {
        var data = new
        {
            action = "unlabeled",
            label = new { name = "in-progress:author" },
            issue = new
            {
                number = 42,
                title = "Test issue",
                body = "Issue body",
                labels = Array.Empty<object>(),
                assignee = (object?)null,
                user = new { login = "opener" },
            },
            repository = new
            {
                name = "test-repo",
                full_name = "owner/test-repo",
                owner = new { login = "owner" }
            }
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = _handler.TranslateEvent("issues", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("changed_label").GetString().ShouldBe("in-progress:author");
    }

    [Theory]
    [InlineData("synchronize", "code_change")]
    [InlineData("ready_for_review", "review_request")]
    [InlineData("converted_to_draft", "lifecycle")]
    [InlineData("closed", "lifecycle")]
    [InlineData("edited", "edit")]
    public void TranslateEvent_PullRequestExpandedActions_ReturnsDomainMessage(string action, string expectedIntent)
    {
        var payload = CreatePullRequestPayload(action);

        var message = _handler.TranslateEvent("pull_request", payload);

        message.ShouldNotBeNull();
        message!.Type.ShouldBe(MessageType.Domain);
        message.Payload.GetProperty("intent").GetString().ShouldBe(expectedIntent);
        message.Payload.GetProperty("action").GetString().ShouldBe(action);
    }

    [Fact]
    public void TranslateEvent_PullRequestClosedMerged_ExposesMergedFlag()
    {
        var data = new
        {
            action = "closed",
            pull_request = new
            {
                number = 10,
                title = "Test PR",
                body = "PR body",
                state = "closed",
                merged = true,
                draft = false,
                head = new { @ref = "feature-branch" },
                @base = new { @ref = "main" },
                user = new { login = "author" },
            },
            repository = new
            {
                name = "test-repo",
                full_name = "owner/test-repo",
                owner = new { login = "owner" }
            }
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = _handler.TranslateEvent("pull_request", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("pull_request").GetProperty("merged").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData("edited")]
    [InlineData("deleted")]
    public void TranslateEvent_IssueCommentExpandedActions_ReturnsDomainMessage(string action)
    {
        var payload = CreateCommentPayloadWithAction(action);

        var message = _handler.TranslateEvent("issue_comment", payload);

        message.ShouldNotBeNull();
        message!.Type.ShouldBe(MessageType.Domain);
        message.Payload.GetProperty("action").GetString().ShouldBe(action);
        message.Payload.GetProperty("intent").GetString().ShouldBe("feedback");
    }

    [Fact]
    public void TranslateEvent_Message_HasCorrectFromAddress()
    {
        var payload = CreateIssuePayload("opened");

        var message = _handler.TranslateEvent("issues", payload);

        message.ShouldNotBeNull();
        message!.From.Scheme.ShouldBe("connector");
        // Pinned synthetic Guid for the GitHub connector sentinel address
        // (greppable as ASCII "github" in the trailing 12 hex chars).
        message.From.Path.ShouldBe("00000000000000000000006769746875");
    }

    [Fact]
    public void TranslateEvent_WithConfiguredTargetUnit_RoutesToUnitScheme()
    {
        var payload = CreateIssuePayload("opened");

        var message = _handler.TranslateEvent("issues", payload);

        message.ShouldNotBeNull();
        message!.To.Scheme.ShouldBe("unit");
        message.To.Path.ShouldBe(TestTeamHex);
    }

    [Fact]
    public void TranslateEvent_WithoutConfiguredTargetUnit_FallsBackToSystemRouter()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var options = new GitHubConnectorOptions { DefaultTargetUnitPath = string.Empty };
        var handler = new GitHubWebhookHandler(options, loggerFactory);

        var payload = CreateIssuePayload("opened");

        var message = handler.TranslateEvent("issues", payload);

        message.ShouldNotBeNull();
        message!.To.Scheme.ShouldBe("system");
        // Pinned synthetic Guid for the fallback router sentinel address
        // (greppable as ASCII "router" in the trailing 12 hex chars).
        message.To.Path.ShouldBe("00000000000000000000726f75746572");
    }

    [Fact]
    public void TranslateEvent_PullRequestReviewSubmitted_ReturnsDomainMessage()
    {
        var data = new
        {
            action = "submitted",
            review = new
            {
                id = 900L,
                state = "approved",
                body = "LGTM",
                user = new { login = "reviewer" },
                submitted_at = "2026-04-13T10:00:00Z",
            },
            pull_request = new
            {
                number = 10,
                title = "Add thing",
                user = new { login = "author" },
            },
            repository = new
            {
                name = "test-repo",
                full_name = "owner/test-repo",
                owner = new { login = "owner" }
            }
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = _handler.TranslateEvent("pull_request_review", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("action").GetString().ShouldBe("submitted");
        message.Payload.GetProperty("review").GetProperty("state").GetString().ShouldBe("approved");
        message.Payload.GetProperty("review").GetProperty("reviewer").GetString().ShouldBe("reviewer");
        message.Payload.GetProperty("pull_request").GetProperty("number").GetInt32().ShouldBe(10);
    }

    [Theory]
    [InlineData("edited")]
    [InlineData("dismissed")]
    public void TranslateEvent_PullRequestReviewExpandedActions_ReturnsDomainMessage(string action)
    {
        var data = new
        {
            action,
            review = new
            {
                id = 900L,
                state = "commented",
                body = "note",
                user = new { login = "reviewer" },
            },
            pull_request = new
            {
                number = 10,
                title = "Add thing",
                user = new { login = "author" },
            },
            repository = new
            {
                name = "test-repo",
                full_name = "owner/test-repo",
                owner = new { login = "owner" }
            }
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = _handler.TranslateEvent("pull_request_review", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("action").GetString().ShouldBe(action);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("edited")]
    [InlineData("deleted")]
    public void TranslateEvent_PullRequestReviewComment_ReturnsDomainMessage(string action)
    {
        var data = new
        {
            action,
            comment = new
            {
                id = 123L,
                body = "nit: rename this",
                path = "src/Foo.cs",
                position = 12,
                diff_hunk = "@@ -1 +1 @@",
                commit_id = "abc",
                user = new { login = "reviewer" },
            },
            pull_request = new
            {
                number = 10,
                title = "Add thing",
            },
            repository = new
            {
                name = "test-repo",
                full_name = "owner/test-repo",
                owner = new { login = "owner" }
            }
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = _handler.TranslateEvent("pull_request_review_comment", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("action").GetString().ShouldBe(action);
        message.Payload.GetProperty("comment").GetProperty("path").GetString().ShouldBe("src/Foo.cs");
        message.Payload.GetProperty("comment").GetProperty("position").GetInt32().ShouldBe(12);
    }

    [Theory]
    [InlineData("resolved", true)]
    [InlineData("unresolved", false)]
    public void TranslateEvent_PullRequestReviewThread_ReturnsDomainMessage(string action, bool expectedResolved)
    {
        var data = new
        {
            action,
            thread = new
            {
                id = 7777L,
                node_id = "PRRT_abc",
            },
            pull_request = new { number = 10, title = "Add thing" },
            repository = new
            {
                name = "test-repo",
                full_name = "owner/test-repo",
                owner = new { login = "owner" }
            }
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = _handler.TranslateEvent("pull_request_review_thread", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("action").GetString().ShouldBe(action);
        message.Payload.GetProperty("thread").GetProperty("resolved").GetBoolean().ShouldBe(expectedResolved);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("deleted")]
    [InlineData("unsuspend")]
    public void TranslateEvent_InstallationLifecycle_ReturnsDomainMessage(string action)
    {
        var data = new
        {
            action,
            installation = new
            {
                id = 12345L,
                account = new { login = "acme", type = "Organization" },
                repository_selection = "selected",
            },
            repositories = new[] { new { id = 1L, name = "repo-a", full_name = "acme/repo-a", @private = false } },
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = _handler.TranslateEvent("installation", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("action").GetString().ShouldBe(action);
        message.Payload.GetProperty("installation").GetProperty("id").GetInt64().ShouldBe(12345L);
        message.Payload.GetProperty("installation").GetProperty("account").GetString().ShouldBe("acme");
        message.Payload.GetProperty("repositories").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public void TranslateEvent_InstallationSuspended_IncludesSuspensionReason()
    {
        var data = new
        {
            action = "suspend",
            installation = new
            {
                id = 12345L,
                account = new { login = "acme", type = "Organization" },
                repository_selection = "selected",
                suspended_at = "2026-04-13T08:00:00Z",
                suspended_by = new { login = "org-admin" },
            },
            sender = new { login = "org-admin" },
            suspended_by = new { login = "org-admin" },
            repositories = Array.Empty<object>(),
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = _handler.TranslateEvent("installation", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("installation").GetProperty("suspended_by").GetString().ShouldBe("org-admin");
        message.Payload.GetProperty("installation").GetProperty("suspended_at").GetString().ShouldBe("2026-04-13T08:00:00Z");
        message.Payload.GetProperty("suspension_reason").GetString().ShouldBe("suspended_by_org-admin");
    }

    [Fact]
    public void TranslateEvent_InstallationRepositoriesAdded_IncludesAddedList()
    {
        var data = new
        {
            action = "added",
            installation = new
            {
                id = 12345L,
                account = new { login = "acme" },
                repository_selection = "selected",
            },
            repositories_added = new[]
            {
                new { id = 2L, name = "repo-b", full_name = "acme/repo-b", @private = false },
                new { id = 3L, name = "repo-c", full_name = "acme/repo-c", @private = true },
            },
            repositories_removed = Array.Empty<object>(),
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = _handler.TranslateEvent("installation_repositories", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("added_repositories").GetArrayLength().ShouldBe(2);
        message.Payload.GetProperty("removed_repositories").GetArrayLength().ShouldBe(0);
        message.Payload.GetProperty("added_repositories")[0].GetProperty("full_name").GetString().ShouldBe("acme/repo-b");
    }

    [Fact]
    public void TranslateEvent_InstallationRepositoriesRemoved_IncludesRemovedList()
    {
        var data = new
        {
            action = "removed",
            installation = new
            {
                id = 12345L,
                account = new { login = "acme" },
                repository_selection = "selected",
            },
            repositories_added = Array.Empty<object>(),
            repositories_removed = new[]
            {
                new { id = 9L, name = "repo-x", full_name = "acme/repo-x", @private = false },
            },
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = _handler.TranslateEvent("installation_repositories", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("removed_repositories").GetArrayLength().ShouldBe(1);
        message.Payload.GetProperty("removed_repositories")[0].GetProperty("name").GetString().ShouldBe("repo-x");
    }

    [Fact]
    public void TranslateEvent_IssueLabeled_WithConfiguredStateLabel_AttachesStateTransition()
    {
        // Issue starts in needs-triage, gets moved to in-progress. GitHub sends
        // the post-change labels on labeled events — "needs-triage" stays in the
        // array here because in practice the coordinator removes it in a second
        // API call. We still derive the transition from the changed label.
        var data = new
        {
            action = "labeled",
            label = new { name = "in-progress" },
            issue = new
            {
                number = 42,
                title = "Bug",
                body = "issue body",
                labels = new[] { new { name = "needs-triage" }, new { name = "in-progress" } },
                assignee = (object?)null,
                user = new { login = "opener" },
            },
            repository = new
            {
                name = "test-repo",
                full_name = "owner/test-repo",
                owner = new { login = "owner" }
            }
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = _handler.TranslateEvent("issues", payload);

        message.ShouldNotBeNull();
        var transition = message!.Payload.GetProperty("state_transition");
        transition.ValueKind.ShouldBe(JsonValueKind.Object);
        transition.GetProperty("from").GetString().ShouldBe("needs-triage");
        transition.GetProperty("to").GetString().ShouldBe("in-progress");
        transition.GetProperty("trigger").GetString().ShouldBe("labeled");
        transition.GetProperty("legal").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void TranslateEvent_IssueLabeled_WithUnrelatedLabel_NoStateTransition()
    {
        var data = new
        {
            action = "labeled",
            label = new { name = "documentation" },
            issue = new
            {
                number = 42,
                title = "Bug",
                body = "issue body",
                labels = new[] { new { name = "documentation" } },
                assignee = (object?)null,
                user = new { login = "opener" },
            },
            repository = new
            {
                name = "test-repo",
                full_name = "owner/test-repo",
                owner = new { login = "owner" }
            }
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = _handler.TranslateEvent("issues", payload);

        message.ShouldNotBeNull();
        var transition = message!.Payload.GetProperty("state_transition");
        transition.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void TranslateEvent_IssueLabeled_WithOverriddenStateMachine_DerivesFromOverride()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var options = new GitHubConnectorOptions { DefaultTargetUnitPath = TestSlugIds.HexFor("team") };

        // Custom vocabulary — "needs-triage" is not in the state set anymore.
        var machine = new LabelStateMachine(new LabelStateMachineOptions
        {
            States = ["queued", "working", "done"],
            Transitions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["queued"] = ["working"],
                ["working"] = ["done"],
                ["done"] = [],
            },
            InitialState = "queued",
        });
        var handler = new GitHubWebhookHandler(options, loggerFactory, machine);

        var data = new
        {
            action = "labeled",
            label = new { name = "working" },
            issue = new
            {
                number = 42,
                title = "Bug",
                body = "body",
                labels = new[] { new { name = "queued" }, new { name = "working" } },
                assignee = (object?)null,
                user = new { login = "opener" },
            },
            repository = new
            {
                name = "r",
                full_name = "o/r",
                owner = new { login = "o" }
            }
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = handler.TranslateEvent("issues", payload);

        message.ShouldNotBeNull();
        var transition = message!.Payload.GetProperty("state_transition");
        transition.GetProperty("from").GetString().ShouldBe("queued");
        transition.GetProperty("to").GetString().ShouldBe("working");
        transition.GetProperty("legal").GetBoolean().ShouldBeTrue();

        // needs-triage is NOT a state label under this override; label-change
        // events for it must not emit a transition.
        var unrelated = new
        {
            action = "labeled",
            label = new { name = "needs-triage" },
            issue = new
            {
                number = 42,
                title = "Bug",
                body = "body",
                labels = new[] { new { name = "needs-triage" } },
                assignee = (object?)null,
                user = new { login = "opener" },
            },
            repository = new
            {
                name = "r",
                full_name = "o/r",
                owner = new { login = "o" }
            }
        };
        var unrelatedPayload = JsonSerializer.SerializeToElement(unrelated);
        var unrelatedMessage = handler.TranslateEvent("issues", unrelatedPayload);
        unrelatedMessage.ShouldNotBeNull();
        unrelatedMessage!.Payload.GetProperty("state_transition").ValueKind.ShouldBe(JsonValueKind.Null);
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

    private static JsonElement CreateCommentPayload() => CreateCommentPayloadWithAction("created");

    private static JsonElement CreateCommentPayloadWithAction(string action)
    {
        var data = new
        {
            action,
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