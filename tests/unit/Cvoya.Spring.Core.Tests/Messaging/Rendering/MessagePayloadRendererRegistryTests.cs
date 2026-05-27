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
/// Tests for <see cref="MessagePayloadRendererRegistry"/> and the
/// built-in renderer set (#2843). Verifies the contract pinned by the
/// PR: every well-known payload shape resolves to the same canonical
/// text regardless of consumer, priority orders ties deterministically,
/// the <see cref="IMessagePayloadRenderer.TargetType"/> pre-filter
/// narrows the search, and an unrecognised payload returns
/// <c>null</c> so callers can decide their own fallback.
/// </summary>
public class MessagePayloadRendererRegistryTests
{
    private static readonly Address Sender = new("agent", new Guid("aaaa1001-0000-0000-0000-000000000001"));
    private static readonly Address Recipient = new("human", new Guid("aaaa1001-0000-0000-0000-000000000002"));

    [Fact]
    public void TryRender_BareString_ReturnsValueVerbatim()
    {
        var registry = BuildDefaultRegistry();
        var msg = NewMessage(JsonSerializer.SerializeToElement("ping"));

        registry.TryRender(msg).ShouldBe("ping");
    }

    [Fact]
    public void TryRender_TextProperty_ReturnsTextValue()
    {
        var registry = BuildDefaultRegistry();
        var msg = NewMessage(JsonSerializer.SerializeToElement(new { text = "from text" }));

        registry.TryRender(msg).ShouldBe("from text");
    }

    [Fact]
    public void TryRender_BodyProperty_ReturnsBodyValue()
    {
        var registry = BuildDefaultRegistry();
        var msg = NewMessage(JsonSerializer.SerializeToElement(new { body = "from body" }));

        registry.TryRender(msg).ShouldBe("from body");
    }

    [Fact]
    public void TryRender_OutputProperty_ReturnsOutputValue()
    {
        var registry = BuildDefaultRegistry();
        var msg = NewMessage(JsonSerializer.SerializeToElement(new { Output = "from Output", ExitCode = 0 }));

        registry.TryRender(msg).ShouldBe("from Output");
    }

    [Fact]
    public void TryRender_ContentProperty_ReturnsContentValue()
    {
        var registry = BuildDefaultRegistry();
        var msg = NewMessage(JsonSerializer.SerializeToElement(new { content = "from content" }));

        registry.TryRender(msg).ShouldBe("from content");
    }

    [Fact]
    public void TryRender_StructuredNonText_ReturnsNull()
    {
        // The renderer registry's contract: "I didn't claim this — caller
        // decides what to do." Slack falls back to raw JSON; the timeline
        // drops the body field.
        var registry = BuildDefaultRegistry();
        var msg = NewMessage(JsonSerializer.SerializeToElement(new { Acknowledged = true, count = 7 }));

        registry.TryRender(msg).ShouldBeNull();
    }

    [Fact]
    public void TryRender_UndefinedPayload_ReturnsNull()
    {
        // Control messages can land with `default(JsonElement)` (Cancel,
        // HealthCheck …). No renderer claims this — caller fall-through
        // is correct.
        var registry = BuildDefaultRegistry();
        var msg = NewMessage(default, type: MessageType.Cancel);

        registry.TryRender(msg).ShouldBeNull();
    }

    [Fact]
    public void TryRender_BothTextAndOutput_TextWins()
    {
        // Determinism guard. A payload that names two well-known
        // properties resolves the same way every call — the test pins the
        // priority order so a refactor that flips it is caught.
        var registry = BuildDefaultRegistry();
        var msg = NewMessage(JsonSerializer.SerializeToElement(new { text = "T", Output = "O" }));

        registry.TryRender(msg).ShouldBe("T");
    }

    [Fact]
    public void TryRender_TextHasHigherPriorityThanBody()
    {
        var registry = BuildDefaultRegistry();
        var msg = NewMessage(JsonSerializer.SerializeToElement(new { body = "B", text = "T" }));

        registry.TryRender(msg).ShouldBe("T");
    }

    [Fact]
    public void TryRender_OutputBeatsContent()
    {
        var registry = BuildDefaultRegistry();
        var msg = NewMessage(JsonSerializer.SerializeToElement(new { content = "C", Output = "O" }));

        registry.TryRender(msg).ShouldBe("O");
    }

    [Fact]
    public void TryRender_TargetTypeFilter_Narrows()
    {
        // A renderer constrained to a specific MessageType is only
        // considered for that type. The Default-priority text renderer is
        // type-agnostic, so a Domain message hits it; a HealthCheck
        // message with a string payload also hits the bare-string
        // renderer (also type-agnostic), so we use a payload neither
        // built-in claims and add a HealthCheck-only renderer.
        var custom = new TestRenderer(
            targetType: MessageType.HealthCheck,
            priority: 0,
            propertyName: "status");

        var registry = new MessagePayloadRendererRegistry(new IMessagePayloadRenderer[] { custom });
        var payload = JsonSerializer.SerializeToElement(new { status = "ok" });

        registry.TryRender(NewMessage(payload, type: MessageType.HealthCheck)).ShouldBe("ok");
        registry.TryRender(NewMessage(payload, type: MessageType.Domain)).ShouldBeNull();
    }

    [Fact]
    public void TryRender_HigherPriorityRendererWinsTie()
    {
        // Two custom renderers compete for the same payload shape; the
        // higher Priority wins.
        var low = new TestRenderer(targetType: null, priority: 1, propertyName: "shared", fixedOutput: "LOW");
        var high = new TestRenderer(targetType: null, priority: 99, propertyName: "shared", fixedOutput: "HIGH");

        var registry = new MessagePayloadRendererRegistry(new IMessagePayloadRenderer[] { low, high });
        var msg = NewMessage(JsonSerializer.SerializeToElement(new { shared = "ignored" }));

        registry.TryRender(msg).ShouldBe("HIGH");
    }

    [Fact]
    public void TryRender_RendererReturningNull_FallsThroughToNext()
    {
        // A renderer that claims via CanRender but returns null on
        // Render hands off to the next candidate — the registry does not
        // treat a null Render as "rendering complete."
        var firstClaimerReturnsNull = new NullReturningRenderer(priority: 50);
        var fallback = new TestRenderer(
            targetType: null,
            priority: 10,
            propertyName: "shared",
            fixedOutput: "fallback");

        var registry = new MessagePayloadRendererRegistry(new IMessagePayloadRenderer[]
        {
            firstClaimerReturnsNull,
            fallback,
        });
        var msg = NewMessage(JsonSerializer.SerializeToElement(new { shared = "ignored" }));

        registry.TryRender(msg).ShouldBe("fallback");
    }

    private static IMessagePayloadRendererRegistry BuildDefaultRegistry() =>
        new MessagePayloadRendererRegistry(new IMessagePayloadRenderer[]
        {
            new BareStringPayloadRenderer(),
            new TextPropertyPayloadRenderer(),
            new BodyPropertyPayloadRenderer(),
            new OutputPropertyPayloadRenderer(),
            new ContentPropertyPayloadRenderer(),
        });

    private static Message NewMessage(JsonElement payload, MessageType type = MessageType.Domain) =>
        new Message(
            Guid.NewGuid(),
            Sender,
            Recipient,
            type,
            "thread-1",
            payload,
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Local test-double — accepts a configured property name and emits a
    /// fixed string. Keeps test cases readable when the production
    /// renderer set doesn't have the exact shape we want to assert.
    /// </summary>
    private sealed class TestRenderer : IMessagePayloadRenderer
    {
        private readonly string _propertyName;
        private readonly string? _fixedOutput;

        public TestRenderer(MessageType? targetType, int priority, string propertyName, string? fixedOutput = null)
        {
            TargetType = targetType;
            Priority = priority;
            _propertyName = propertyName;
            _fixedOutput = fixedOutput;
        }

        public MessageType? TargetType { get; }

        public int Priority { get; }

        public bool CanRender(Message message)
            => message.Payload.ValueKind == JsonValueKind.Object
            && message.Payload.TryGetProperty(_propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String;

        public string? Render(Message message)
        {
            if (_fixedOutput is not null)
            {
                return _fixedOutput;
            }

            return message.Payload.TryGetProperty(_propertyName, out var prop)
                ? prop.GetString()
                : null;
        }
    }

    /// <summary>
    /// Test-double that always claims via <see cref="CanRender"/> but
    /// returns <c>null</c> from <see cref="Render"/>. Used to assert the
    /// registry's "Render returned null → try the next candidate"
    /// fall-through.
    /// </summary>
    private sealed class NullReturningRenderer : IMessagePayloadRenderer
    {
        public NullReturningRenderer(int priority) => Priority = priority;

        public MessageType? TargetType => null;

        public int Priority { get; }

        public bool CanRender(Message message) => true;

        public string? Render(Message message) => null;
    }
}
