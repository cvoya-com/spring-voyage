// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.Messaging.Rendering;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Messaging.Rendering;
using Cvoya.Spring.Core.Messaging.Rendering.Renderers;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="A2aTaskPayloadRenderer"/> (#2856). Pins the
/// payload-shape contract the dispatcher constructs from an A2A response
/// and the artifact-→-status-→-history precedence the pre-#2856 in-file
/// extractor relied on. The renderer is consumer-side and pure JSON; no
/// A2A SDK is involved in the test surface.
/// </summary>
public class A2aTaskPayloadRendererTests
{
    private static readonly Address Sender = new("agent", new Guid("aaaa1856-0000-0000-0000-000000000001"));
    private static readonly Address Recipient = new("agent", new Guid("aaaa1856-0000-0000-0000-000000000002"));

    [Fact]
    public void TryRender_TaskWithArtifactTextParts_ReturnsArtifactText()
    {
        // Artifacts win when they carry any text. Multiple text parts and
        // multiple artifacts concatenate with '\n', mirroring the
        // pre-#2856 helper's string.Join("\n", …) over OfType<TextPart>.
        var payload = TaskPayload(new
        {
            artifacts = new object[]
            {
                new
                {
                    artifactId = "art-1",
                    parts = new object[]
                    {
                        new { kind = "text", text = "first" },
                        new { kind = "text", text = "second" },
                    },
                },
                new
                {
                    artifactId = "art-2",
                    parts = new object[]
                    {
                        new { kind = "text", text = "third" },
                    },
                },
            },
        });

        Render(payload).ShouldBe("first\nsecond\nthird");
    }

    [Fact]
    public void TryRender_TaskArtifactsNonTextOnly_FallsThroughToStatus()
    {
        // The pre-#2856 helper used OfType<TextPart> and short-circuited
        // only if the resulting string was non-empty. A file/data-only
        // artifact array falls through to the status message.
        var payload = TaskPayload(new
        {
            artifacts = new object[]
            {
                new
                {
                    artifactId = "art-1",
                    parts = new object[]
                    {
                        new { kind = "file", file = new { uri = "x" } },
                        new { kind = "data", data = new { } },
                    },
                },
            },
            status = new
            {
                state = "completed",
                message = new
                {
                    role = "agent",
                    parts = new object[]
                    {
                        new { kind = "text", text = "from status" },
                    },
                },
            },
        });

        Render(payload).ShouldBe("from status");
    }

    [Fact]
    public void TryRender_TaskWithoutArtifactsButWithStatusMessage_ReturnsStatusText()
    {
        // Mirrors today's helper: when artifacts is null/empty, the status
        // message's parts are the next text source.
        var payload = TaskPayload(new
        {
            status = new
            {
                state = "completed",
                message = new
                {
                    role = "agent",
                    parts = new object[]
                    {
                        new { kind = "text", text = "status-only" },
                    },
                },
            },
        });

        Render(payload).ShouldBe("status-only");
    }

    [Fact]
    public void TryRender_TaskFallsThroughToHistoryAgent_PicksLastAgentMessage()
    {
        // The legacy helper picks history.LastOrDefault(m => m.Role == Agent).
        // Verify the same "last agent role" semantic — interleaved user
        // turns are skipped, and a later agent message wins over an
        // earlier one.
        var payload = TaskPayload(new
        {
            history = new object[]
            {
                new
                {
                    kind = "message",
                    role = "agent",
                    parts = new object[] { new { kind = "text", text = "earlier" } },
                },
                new
                {
                    kind = "message",
                    role = "user",
                    parts = new object[] { new { kind = "text", text = "user turn" } },
                },
                new
                {
                    kind = "message",
                    role = "agent",
                    parts = new object[] { new { kind = "text", text = "latest" } },
                },
            },
        });

        Render(payload).ShouldBe("latest");
    }

    [Fact]
    public void TryRender_TaskHistoryWithNoAgentEntry_ReturnsNull()
    {
        // Pre-#2856 helper returned "" in this case; the dispatcher then
        // mapped "" to null via NullIfEmpty. The renderer registry skips
        // the NullIfEmpty step and returns null directly when nothing
        // claims the payload.
        var payload = TaskPayload(new
        {
            history = new object[]
            {
                new
                {
                    kind = "message",
                    role = "user",
                    parts = new object[] { new { kind = "text", text = "user only" } },
                },
            },
        });

        Render(payload).ShouldBeNull();
    }

    [Fact]
    public void TryRender_TaskHistoryAgentWithNoTextParts_ReturnsNull()
    {
        // History fall-through requires the chosen agent message to carry
        // a text part. A file-only agent message falls through to "no
        // renderable text" — the registry returns null.
        var payload = TaskPayload(new
        {
            history = new object[]
            {
                new
                {
                    kind = "message",
                    role = "agent",
                    parts = new object[]
                    {
                        new { kind = "file", file = new { uri = "x" } },
                    },
                },
            },
        });

        Render(payload).ShouldBeNull();
    }

    [Fact]
    public void TryRender_TaskEmptyShell_ReturnsNull()
    {
        // No artifacts, no status.message, no history — renderer cannot
        // produce text. Returning null lets the dispatcher pass `null` on
        // to RuntimeOutcome.ReasoningTrace, matching ADR-0056's "no
        // synthesised text when there's nothing to report".
        var payload = TaskPayload(new
        {
            status = new { state = "failed" },
        });

        Render(payload).ShouldBeNull();
    }

    [Fact]
    public void TryRender_TaskStatusMessageNonTextParts_FallsThroughToHistory()
    {
        // A status message that carries only non-text parts is treated as
        // "no text here" and falls through to history (matching the
        // pre-#2856 helper's behaviour where ExtractTextFromParts on a
        // file-only Parts list returned "").
        var payload = TaskPayload(new
        {
            status = new
            {
                state = "completed",
                message = new
                {
                    role = "agent",
                    parts = new object[] { new { kind = "file", file = new { uri = "x" } } },
                },
            },
            history = new object[]
            {
                new
                {
                    kind = "message",
                    role = "agent",
                    parts = new object[] { new { kind = "text", text = "from history" } },
                },
            },
        });

        Render(payload).ShouldBe("from history");
    }

    [Fact]
    public void TryRender_A2aMessage_ReturnsConcatenatedTextParts()
    {
        // Bare AgentMessage wrap: top-level parts, no artifacts/status/history.
        // Multiple text parts join with '\n', non-text parts are dropped.
        var payload = WrappedPayload(A2aTaskPayloadRenderer.MessageKind, new
        {
            role = "agent",
            parts = new object[]
            {
                new { kind = "text", text = "hello" },
                new { kind = "file", file = new { uri = "x" } },
                new { kind = "text", text = "world" },
            },
            messageId = "m-1",
        });

        Render(payload).ShouldBe("hello\nworld");
    }

    [Fact]
    public void TryRender_A2aMessageNoTextParts_ReturnsNull()
    {
        var payload = WrappedPayload(A2aTaskPayloadRenderer.MessageKind, new
        {
            role = "agent",
            parts = new object[]
            {
                new { kind = "file", file = new { uri = "x" } },
            },
        });

        Render(payload).ShouldBeNull();
    }

    [Fact]
    public void TryRender_NoKindField_NotClaimed()
    {
        // Defensive: a payload that looks A2A-shaped but lacks the
        // namespaced `kind` discriminator is NOT claimed by this
        // renderer. The dispatcher is the only producer of this shape;
        // anything else falls through to the per-shape renderers.
        var renderer = new A2aTaskPayloadRenderer();
        var msg = NewMessage(JsonSerializer.SerializeToElement(new
        {
            artifacts = new object[] { new { parts = new object[] { new { kind = "text", text = "x" } } } },
        }));

        renderer.CanRender(msg).ShouldBeFalse();
    }

    [Fact]
    public void TryRender_UnknownKindNamespace_NotClaimed()
    {
        var renderer = new A2aTaskPayloadRenderer();
        var msg = NewMessage(JsonSerializer.SerializeToElement(new { kind = "other.task", parts = Array.Empty<object>() }));

        renderer.CanRender(msg).ShouldBeFalse();
    }

    [Fact]
    public void TryRender_PriorityIsBelowBuiltInBand()
    {
        // The A2A payload is namespaced via `kind`, so it can't collide
        // with the bare-string / text / body / Output / content renderers
        // — but pin the priority so a future renumbering catches drift.
        new A2aTaskPayloadRenderer().Priority.ShouldBeLessThan(50);
    }

    [Fact]
    public void TryRender_TargetTypeIsNull_RendererIsTypeAgnostic()
    {
        // The dispatcher synthesises a Message of MessageType.Domain for
        // every A2A response; the renderer must not pre-filter on type.
        new A2aTaskPayloadRenderer().TargetType.ShouldBeNull();
    }

    private static string? Render(JsonElement payload)
    {
        var registry = new MessagePayloadRendererRegistry(new IMessagePayloadRenderer[]
        {
            new BareStringPayloadRenderer(),
            new TextPropertyPayloadRenderer(),
            new BodyPropertyPayloadRenderer(),
            new OutputPropertyPayloadRenderer(),
            new ContentPropertyPayloadRenderer(),
            new A2aTaskPayloadRenderer(),
        });

        return registry.TryRender(NewMessage(payload));
    }

    private static JsonElement TaskPayload(object body)
        => WrappedPayload(A2aTaskPayloadRenderer.TaskKind, body);

    private static JsonElement WrappedPayload(string kind, object body)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body))!.AsObject();
        node["kind"] = kind;
        return JsonSerializer.SerializeToElement(node);
    }

    private static Message NewMessage(JsonElement payload) =>
        new Message(
            Guid.NewGuid(),
            Sender,
            Recipient,
            MessageType.Domain,
            "thread-2856",
            payload,
            DateTimeOffset.UtcNow);
}
