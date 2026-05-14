// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Prompts;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ThreadContextBuilder"/>.
/// </summary>
public class ThreadContextBuilderTests
{
    private static readonly Guid AliceId = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid BobId = new("aaaaaaaa-1111-1111-1111-000000000002");
    private static readonly Guid HumanId = new("aaaaaaaa-1111-1111-1111-000000000003");
    private static readonly Guid ReceiverId = new("aaaaaaaa-1111-1111-1111-000000000004");

    private readonly ThreadContextBuilder _builder = new();

    private static Message CreateMessage(Address from, string text)
    {
        return new Message(
            Guid.NewGuid(),
            from,
            new Address("agent", ReceiverId),
            MessageType.Domain,
            "conv-1",
            JsonSerializer.SerializeToElement(new { text }),
            DateTimeOffset.UtcNow);
    }

    private static Message CreateMessage(Guid senderId, string text) =>
        CreateMessage(new Address("agent", senderId), text);

    /// <summary>
    /// When a display-name map is supplied, prior-turn senders render as
    /// the resolved display name. Pins the #2129 happy path: the prompt
    /// reads <c>[ts] {DisplayName}: {text}</c>.
    /// </summary>
    [Fact]
    public void Build_UsesDisplayNamesWhenSupplied()
    {
        var alice = new Address("agent", AliceId);
        var bob = new Address("agent", BobId);
        var messages = new List<Message>
        {
            CreateMessage(alice, "Hello there"),
            CreateMessage(bob, "Hi Alice"),
        };
        var displayNames = new Dictionary<Address, string>
        {
            [alice] = "Alice the Researcher",
            [bob] = "Bob the Synthesist",
        };

        var result = _builder.Build(messages, null, displayNames);

        result.ShouldContain("Prior Messages");
        result.ShouldContain("Alice the Researcher: Hello there");
        result.ShouldContain("Bob the Synthesist: Hi Alice");
    }

    /// <summary>
    /// When the display-name map is absent or omits a sender, the formatter
    /// falls back to the address's bare scheme literal (e.g. <c>agent</c>,
    /// <c>human</c>) — NEVER the raw <c>scheme://&lt;guid&gt;</c> wire shape
    /// the pre-#2129 formatter emitted. The bare scheme is the worst-case
    /// fallback the issue's acceptance criteria pin: any unresolvable case
    /// is better as <c>[ts] human: hello</c> than the leaked-address
    /// <c>[ts] human://abc123…: hello</c> #2089 was about.
    /// </summary>
    [Fact]
    public void Build_FallsBackToSchemeLiteralWhenDisplayNameMissing()
    {
        var alice = new Address("agent", AliceId);
        var bob = new Address("human", HumanId);
        var messages = new List<Message>
        {
            CreateMessage(alice, "Hello there"),
            CreateMessage(bob, "Hi Alice"),
        };
        // Map present but missing both senders.
        var displayNames = new Dictionary<Address, string>();

        var result = _builder.Build(messages, null, displayNames);

        result.ShouldContain("agent: Hello there");
        result.ShouldContain("human: Hi Alice");
    }

    /// <summary>
    /// A null display-name map is the same fallback shape as a map that
    /// omits the sender — the bare scheme literal, never a raw GUID.
    /// </summary>
    [Fact]
    public void Build_FallsBackToSchemeLiteralWhenMapIsNull()
    {
        var messages = new List<Message>
        {
            CreateMessage(AliceId, "Hello there"),
        };

        var result = _builder.Build(messages, null);

        result.ShouldContain("agent: Hello there");
    }

    /// <summary>
    /// Whitespace / empty resolved display name is treated as "missing" and
    /// falls through to the scheme literal — keeps the non-empty contract
    /// from <see cref="Cvoya.Spring.Core.Security.IParticipantDisplayNameResolver"/>
    /// honoured even if a future implementation slips an empty value into
    /// the map.
    /// </summary>
    [Fact]
    public void Build_TreatsEmptyDisplayNameAsMissing()
    {
        var alice = new Address("agent", AliceId);
        var messages = new List<Message>
        {
            CreateMessage(alice, "Hello there"),
        };
        var displayNames = new Dictionary<Address, string>
        {
            [alice] = "   ",
        };

        var result = _builder.Build(messages, null, displayNames);

        result.ShouldContain("agent: Hello there");
    }

    /// <summary>
    /// Regression pin for #2129 — the precise wire shape the issue is
    /// fighting. The pre-fix formatter emitted
    /// <c>scheme://&lt;guid&gt;</c>, which weak LLMs were observed mimicking
    /// on output. The new formatter MUST never emit a <c>://</c> substring
    /// in the prior-message section, regardless of whether display names
    /// are supplied or not.
    /// </summary>
    [Fact]
    public void Build_NeverEmitsSchemeUriPrefix()
    {
        var messages = new List<Message>
        {
            CreateMessage(new Address("human", HumanId), "Hello there"),
            CreateMessage(new Address("agent", AliceId), "Hi"),
        };

        var withResolved = _builder.Build(
            messages,
            null,
            new Dictionary<Address, string>
            {
                [new Address("human", HumanId)] = "Savas",
                [new Address("agent", AliceId)] = "Ada",
            });
        var withFallback = _builder.Build(messages, null);

        withResolved.ShouldNotContain("://");
        withFallback.ShouldNotContain("://");
    }

    /// <summary>
    /// Regression pin for #2129 — the no-dash GUID hex that the pre-fix
    /// formatter was leaking into the prompt. Even when the display-name
    /// map is unavailable, the raw 32-char hex MUST NOT appear in the
    /// rendered output.
    /// </summary>
    [Fact]
    public void Build_NeverEmitsRawGuidHex()
    {
        var messages = new List<Message>
        {
            CreateMessage(AliceId, "Hello there"),
        };

        var result = _builder.Build(messages, null);

        result.ShouldNotContain(AliceId.ToString("N"));
        result.ShouldNotContain(AliceId.ToString());
    }

    /// <summary>
    /// Verifies that checkpoint state is included in the output.
    /// </summary>
    [Fact]
    public void Build_IncludesCheckpointState()
    {
        var result = _builder.Build([], "Step 3 of 5 completed");

        result.ShouldContain("Last Checkpoint");
        result.ShouldContain("Step 3 of 5 completed");
    }

    /// <summary>
    /// Verifies that empty thread produces an empty string.
    /// </summary>
    [Fact]
    public void Build_HandlesEmptyThread()
    {
        var result = _builder.Build([], null);

        result.ShouldBeEmpty();
    }

    /// <summary>
    /// Regression — #480 step 2 surfaced this while running the dapr-agent
    /// scenario end-to-end. The CLI `message send` path serialises the user
    /// text as a bare JSON string (UntypedString on the wire); the builder
    /// used to call JsonElement.TryGetProperty on the non-object payload and
    /// crash with InvalidOperationException. ExtractText now accepts every
    /// ValueKind without throwing.
    /// </summary>
    [Fact]
    public void Build_AcceptsBareStringPayload()
    {
        var message = new Message(
            Guid.NewGuid(),
            new Address("agent", HumanId),
            new Address("agent", ReceiverId),
            MessageType.Domain,
            "conv-1",
            JsonSerializer.SerializeToElement("Say hello in one sentence."),
            DateTimeOffset.UtcNow);

        var result = _builder.Build([message], null);

        result.ShouldContain("Say hello in one sentence.");
    }

    /// <summary>
    /// The A2A-backed path wraps the message in { Task: "..." }; both shapes
    /// must produce readable history to keep thread context useful.
    /// </summary>
    [Fact]
    public void Build_AcceptsTaskPayloadShape()
    {
        var message = new Message(
            Guid.NewGuid(),
            new Address("agent", HumanId),
            new Address("agent", ReceiverId),
            MessageType.Domain,
            "conv-1",
            JsonSerializer.SerializeToElement(new { Task = "do-work" }),
            DateTimeOffset.UtcNow);

        var result = _builder.Build([message], null);

        result.ShouldContain("do-work");
    }
}
