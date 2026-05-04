// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="MessageReceivedDetails"/> — the helper that
/// stamps the message envelope onto activity-event Details JSON and builds the
/// non-leaky summary line emitted by every actor's <c>MessageReceived</c>
/// projection (#1209, #1636).
/// </summary>
public class MessageReceivedDetailsTests
{
    [Fact]
    public void BuildSummary_StringPayload_ReturnsBodyVerbatim()
    {
        var msg = CreateMessage(JsonSerializer.SerializeToElement("Approve merge?"));

        var summary = MessageReceivedDetails.BuildSummary(msg);

        summary.ShouldBe("Approve merge?");
    }

    [Fact]
    public void BuildSummary_AgentReplyOutputShape_ReturnsOutputText()
    {
        // The dispatcher wraps agent replies as { Output, ExitCode } —
        // BuildSummary unwraps to the natural-language reply.
        var payload = JsonSerializer.SerializeToElement(new
        {
            Output = "Looks good — shipping.",
            ExitCode = 0,
        });
        var msg = CreateMessage(payload);

        var summary = MessageReceivedDetails.BuildSummary(msg);

        summary.ShouldBe("Looks good — shipping.");
    }

    [Fact]
    public void BuildSummary_ControlMessage_ReturnsPlaceholderWithoutGuid()
    {
        // Control / structured-payload messages have no reader-visible text;
        // fall back to a short type label that carries no GUIDs and no
        // addresses.
        var payload = JsonSerializer.SerializeToElement(new { Acknowledged = true });
        var msg = CreateMessage(payload, type: MessageType.HealthCheck);

        var summary = MessageReceivedDetails.BuildSummary(msg);

        summary.ShouldBe("Health check received");
        summary.ShouldNotContain(msg.Id.ToString());
        summary.ShouldNotContain(msg.From.Path);
    }

    [Fact]
    public void BuildSummary_DomainMessageWithStructuredPayload_ReturnsNeutralPlaceholder()
    {
        // Ack envelopes / error envelopes / structured non-text payloads
        // resolve to "Message received" — never the legacy "Received Domain
        // message <uuid> from <address>" template.
        var payload = JsonSerializer.SerializeToElement(new { Acknowledged = true });
        var msg = CreateMessage(payload);

        var summary = MessageReceivedDetails.BuildSummary(msg);

        summary.ShouldBe("Message received");
        summary.ShouldNotContain(msg.Id.ToString());
        summary.ShouldNotContain(msg.From.Path);
    }

    [Fact]
    public void BuildSummary_LongTextPayload_TruncatesWithEllipsis()
    {
        // Summary is a glance-line; the full body still rides on Details.body.
        var longText = new string('x', 500);
        var msg = CreateMessage(JsonSerializer.SerializeToElement(longText));

        var summary = MessageReceivedDetails.BuildSummary(msg);

        summary.Length.ShouldBeLessThanOrEqualTo(160);
        summary.ShouldEndWith("…");
    }

    [Fact]
    public void BuildSummary_NeverEmitsLegacyEnvelopeTemplate()
    {
        // Hard regression guard: across every shape the helper produces, the
        // result must never start with "Received " (the legacy template
        // prefix) when the body is structurally absent. The neutral
        // placeholder ("Message received", "Health check received", …) ends
        // with "received" but does not begin with "Received {Type} message".
        var shapes = new[]
        {
            JsonSerializer.SerializeToElement("hello"),
            JsonSerializer.SerializeToElement(new { Acknowledged = true }),
            JsonSerializer.SerializeToElement(new { Output = "ok", ExitCode = 0 }),
            JsonSerializer.SerializeToElement(new { Error = "boom" }),
        };

        foreach (var payload in shapes)
        {
            var msg = CreateMessage(payload);
            var summary = MessageReceivedDetails.BuildSummary(msg);

            summary.ShouldNotStartWith("Received Domain message");
            summary.ShouldNotContain(msg.Id.ToString());
            summary.ShouldNotContain(msg.From.Path);
        }
    }

    private static Message CreateMessage(JsonElement payload, MessageType type = MessageType.Domain)
    {
        return new Message(
            Guid.NewGuid(),
            Address.For("agent", Guid.NewGuid().ToString("N")),
            Address.For("human", Guid.NewGuid().ToString("N")),
            type,
            "thread-1",
            payload,
            DateTimeOffset.UtcNow);
    }
}