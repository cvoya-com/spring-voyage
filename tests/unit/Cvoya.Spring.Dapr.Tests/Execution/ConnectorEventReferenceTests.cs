// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ConnectorEventReference"/> — the connector-agnostic
/// reader the routing-decision activity (#2560) uses to stamp the connector
/// event type and external entity reference onto a <c>DecisionMade</c> event.
/// </summary>
public class ConnectorEventReferenceTests
{
    private static Message MessageWithPayload(object payload)
        => new(
            Guid.NewGuid(),
            new Address(Address.ConnectorScheme, new Guid("00000000-0000-0000-0000-006769746875")),
            Address.For(Address.UnitScheme, TestSlugIds.HexFor("unit")),
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(payload),
            DateTimeOffset.UtcNow);

    [Fact]
    public void From_IssueEvent_ReadsSourceActionAndIssueNumber()
    {
        var message = MessageWithPayload(new
        {
            source = "github",
            intent = "label_change",
            action = "unlabeled",
            issue = new { number = 2535, title = "Flaky test" },
        });

        var reference = ConnectorEventReference.From(message);

        reference.EventType.ShouldBe("github.unlabeled");
        reference.EntityKind.ShouldBe("issue");
        reference.EntityReference.ShouldBe("2535");
    }

    [Fact]
    public void From_PullRequestEvent_ReadsPullRequestNumber()
    {
        var message = MessageWithPayload(new
        {
            source = "github",
            action = "opened",
            pull_request = new { number = 412 },
        });

        var reference = ConnectorEventReference.From(message);

        reference.EventType.ShouldBe("github.opened");
        reference.EntityKind.ShouldBe("pull_request");
        reference.EntityReference.ShouldBe("412");
    }

    [Fact]
    public void From_NoAction_FallsBackToIntent()
    {
        var message = MessageWithPayload(new { source = "github", intent = "review_request" });

        var reference = ConnectorEventReference.From(message);

        reference.EventType.ShouldBe("github.review_request");
        reference.EntityKind.ShouldBeNull();
        reference.EntityReference.ShouldBeNull();
    }

    [Fact]
    public void From_NonConnectorShapedPayload_ReturnsAllNull()
    {
        var message = MessageWithPayload(new { text = "hello" });

        var reference = ConnectorEventReference.From(message);

        reference.EventType.ShouldBeNull();
        reference.EntityKind.ShouldBeNull();
        reference.EntityReference.ShouldBeNull();
    }

    [Fact]
    public void From_StringPayload_ReturnsAllNull()
    {
        var message = MessageWithPayload("a bare string payload");

        var reference = ConnectorEventReference.From(message);

        reference.ShouldBe(default(ConnectorEventReference));
    }
}
