// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

public class GitHubWebhookHandlerProjectsV2Tests
{
    private readonly GitHubWebhookHandler _handler;

    public GitHubWebhookHandlerProjectsV2Tests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var options = new GitHubConnectorOptions { DefaultTargetUnitPath = "test-team" };
        _handler = new GitHubWebhookHandler(options, loggerFactory);
    }

    [Fact]
    public void TranslateEvent_ProjectsV2Created_EmitsLifecycleIntent()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            action = "created",
            projects_v2 = new
            {
                id = 42L,
                node_id = "PVT_1",
                number = 7,
                title = "Delivery",
                closed = false,
            },
            organization = new { login = "acme" },
        });

        var message = _handler.TranslateEvent("projects_v2", payload);

        message.ShouldNotBeNull();
        message!.Type.ShouldBe(MessageType.Domain);
        message.Payload.GetProperty("intent").GetString().ShouldBe("project_lifecycle");
        message.Payload.GetProperty("action").GetString().ShouldBe("created");
        message.Payload.GetProperty("owner").GetString().ShouldBe("acme");
        message.Payload.GetProperty("project").GetProperty("id").GetString().ShouldBe("PVT_1");
        message.Payload.GetProperty("project").GetProperty("number").GetInt32().ShouldBe(7);
        message.Payload.GetProperty("project").GetProperty("title").GetString().ShouldBe("Delivery");
    }

    [Fact]
    public void TranslateEvent_ProjectsV2ItemEdited_CarriesFieldValueChanges()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            action = "edited",
            projects_v2_item = new
            {
                id = 100L,
                node_id = "PVTI_1",
                project_node_id = "PVT_1",
                content_type = "Issue",
                content_node_id = "I_1",
            },
            changes = new
            {
                field_value = new
                {
                    field_node_id = "F_STATUS",
                    field_type = "single_select",
                    from = new { id = "OPT_T", name = "Todo" },
                    to = new { id = "OPT_D", name = "Done" },
                },
            },
            organization = new { login = "acme" },
        });

        var message = _handler.TranslateEvent("projects_v2_item", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("intent").GetString().ShouldBe("project_item_change");
        message.Payload.GetProperty("action").GetString().ShouldBe("edited");
        message.Payload.GetProperty("project_id").GetString().ShouldBe("PVT_1");
        message.Payload.GetProperty("item").GetProperty("id").GetString().ShouldBe("PVTI_1");
        message.Payload.GetProperty("item").GetProperty("content_type").GetString().ShouldBe("Issue");

        var fieldChanges = message.Payload.GetProperty("field_value_changes");
        fieldChanges.GetProperty("field_node_id").GetString().ShouldBe("F_STATUS");
        fieldChanges.GetProperty("to").GetProperty("name").GetString().ShouldBe("Done");
    }

    [Fact]
    public void TranslateEvent_ProjectsV2ItemArchived_UsesLifecycleIntent()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            action = "archived",
            projects_v2_item = new
            {
                id = 100L,
                node_id = "PVTI_1",
                project_node_id = "PVT_1",
                content_type = "Issue",
                archived_at = "2026-04-13T12:00:00Z",
            },
            organization = new { login = "acme" },
        });

        var message = _handler.TranslateEvent("projects_v2_item", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("intent").GetString().ShouldBe("project_item_lifecycle");
        message.Payload.GetProperty("item").GetProperty("archived").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void TranslateEvent_ProjectsV2UnknownAction_ReturnsNull()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            action = "some_future_action",
            projects_v2 = new { id = 42L, node_id = "PVT_1", number = 7, title = "x", closed = false },
            organization = new { login = "acme" },
        });

        var message = _handler.TranslateEvent("projects_v2", payload);

        message.ShouldBeNull();
    }

    [Fact]
    public void TranslateEvent_ProjectsV2ItemReordered_EmitsProjectItemChange()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            action = "reordered",
            projects_v2_item = new
            {
                id = 100L,
                node_id = "PVTI_1",
                project_node_id = "PVT_1",
                content_type = "Issue",
            },
            organization = new { login = "acme" },
        });

        var message = _handler.TranslateEvent("projects_v2_item", payload);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("intent").GetString().ShouldBe("project_item_change");
    }
}