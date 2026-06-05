// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Prompts;

using Shouldly;

using Xunit;

/// <summary>
/// Contract tests for the inbound-envelope rendering (#2746). Pins the
/// shape the dispatcher hands to the runtime: bullet header naming
/// <c>from</c>/<c>to</c>/<c>participants</c>/<c>message_id</c>/<c>timestamp</c>/<c>payload</c>,
/// followed by a fenced JSON appendix that carries the structured payload
/// verbatim, followed by the "decide what to do, call sv.messaging.respond_to
/// or sv.messaging.send" instruction. The envelope never names
/// <c>thread_id</c> (per #2747). <c>participants</c> is the routable roster
/// added by ADR-0064.
/// </summary>
public class InboundEnvelopeBuilderTests
{
    private static readonly Address Sender =
        new(Address.HumanScheme, new Guid("11111111-2222-3333-4444-555555555555"));
    private static readonly Address Self =
        new(Address.AgentScheme, new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private static readonly Address Other =
        new(Address.AgentScheme, new Guid("99999999-8888-7777-6666-555555555555"));

    private static readonly DateTimeOffset KnownTimestamp =
        new(2026, 5, 24, 12, 34, 56, TimeSpan.Zero);

    private static string Render(
        JsonElement payload,
        string? senderDisplayName = "Alice",
        IReadOnlyList<Address>? recipients = null,
        IReadOnlyList<Address>? participants = null,
        Guid? inReplyTo = null)
    {
        var inbound = new Message(
            Guid.NewGuid(),
            Sender,
            Self,
            MessageType.Domain,
            ThreadId: null,
            payload,
            KnownTimestamp,
            InReplyTo: inReplyTo);

        var to = recipients ?? new[] { Self };
        // Default roster: the recipients plus the routable sender (ADR-0064).
        var roster = participants ?? to.Concat(new[] { Sender }).ToList();

        var renderMethod = typeof(InboundEnvelopeBuilder).GetMethod(
            "Render", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        return (string)renderMethod.Invoke(
            null,
            new object?[] { inbound, senderDisplayName, to, roster })!;
    }

    [Fact]
    public void Render_OpensWithReceivedMessageHeader()
    {
        var rendered = Render(JsonSerializer.SerializeToElement("hello"));

        rendered.ShouldStartWith("You received a message.");
    }

    [Fact]
    public void Render_NamesFromWithDisplayNameWhenResolved()
    {
        var rendered = Render(JsonSerializer.SerializeToElement("hello"));

        rendered.ShouldContain($"- from: {Sender} (Alice)");
    }

    [Fact]
    public void Render_OmitsParentheticDisplayName_WhenSenderUnknown()
    {
        var rendered = Render(
            JsonSerializer.SerializeToElement("hello"),
            senderDisplayName: null);

        rendered.ShouldContain($"- from: {Sender}");
        rendered.ShouldNotContain($"- from: {Sender} (");
    }

    [Fact]
    public void Render_NamesAllRecipientsOnToLine()
    {
        var rendered = Render(
            JsonSerializer.SerializeToElement("hello"),
            recipients: new[] { Self, Other });

        rendered.ShouldContain($"- to: [{Self}, {Other}]");
    }

    [Fact]
    public void Render_NamesParticipantsRoster_IncludingRoutableSender()
    {
        // ADR-0064 — `participants` is the full routable roster: `to`
        // together with the routable sender. It is the set
        // sv.messaging.respond_to delivers to. Distinct from `to`, which
        // excludes the sender.
        var rendered = Render(
            JsonSerializer.SerializeToElement("hello"),
            recipients: new[] { Self, Other },
            participants: new[] { Self, Other, Sender });

        rendered.ShouldContain($"- to: [{Self}, {Other}]");
        rendered.ShouldContain($"- participants: [{Self}, {Other}, {Sender}]");
    }

    [Fact]
    public void Render_StringPayloadAppearsInlineInBulletHeader()
    {
        var rendered = Render(JsonSerializer.SerializeToElement("hello back to you"));

        rendered.ShouldContain("- payload:");
        rendered.ShouldContain("hello back to you");
    }

    [Fact]
    public void Render_StructuredPayloadDeferredToJsonAppendix()
    {
        // Connector webhook events arrive as object payloads with neither
        // `text` nor `Task` — the bullet header points at the JSON
        // appendix instead of dumping the object inline.
        var webhookEvent = JsonSerializer.SerializeToElement(new
        {
            @event = "issues",
            action = "opened",
            issue = new { number = 2746, title = "Structured envelope" },
        });

        var rendered = Render(webhookEvent);

        rendered.ShouldContain("<structured payload — see JSON appendix>");
        rendered.ShouldContain("```json");
        // The JSON appendix carries the structured payload verbatim.
        rendered.ShouldContain("\"event\": \"issues\"");
        rendered.ShouldContain("\"action\": \"opened\"");
        rendered.ShouldContain("\"number\": 2746");
    }

    [Fact]
    public void Render_DoesNotMentionThreadId()
    {
        // #2747 — the agent never sees a thread_id in the envelope.
        var rendered = Render(JsonSerializer.SerializeToElement("hi"));

        rendered.ShouldNotContain("thread_id");
        rendered.ShouldNotContain("threadId");
    }

    [Fact]
    public void Render_JsonAppendixCarriesEnvelopeFields()
    {
        var rendered = Render(
            JsonSerializer.SerializeToElement("hi"),
            recipients: new[] { Self, Other },
            participants: new[] { Self, Other, Sender });

        rendered.ShouldContain("```json");
        rendered.ShouldContain($"\"from\": \"{Sender}\"");
        rendered.ShouldContain("\"from_display_name\": \"Alice\"");
        rendered.ShouldContain("\"to\":");
        rendered.ShouldContain("\"participants\":");
        rendered.ShouldContain(KnownTimestamp.ToString("O", CultureInfo.InvariantCulture));
        rendered.ShouldContain("\"payload\": \"hi\"");
    }

    [Fact]
    public void Render_CarriesInReplyTo_WhenSet_OmitsWhenNull()
    {
        // ADR-0066 §5: a reply names the message it answers (in_reply_to) so a
        // sender can correlate fan-out replies; an original message omits it.
        var answered = Guid.NewGuid();
        var reply = Render(JsonSerializer.SerializeToElement("hi"), inReplyTo: answered);
        reply.ShouldContain($"\"in_reply_to\": \"{GuidFormatter.Format(answered)}\"");
        reply.ShouldContain("- in_reply_to: ");

        var original = Render(JsonSerializer.SerializeToElement("hi"));
        original.ShouldNotContain("in_reply_to");
    }

    [Fact]
    public void Render_PointsRuntimeAtContinuationAndSend()
    {
        var rendered = Render(JsonSerializer.SerializeToElement("hi"));

        // ADR-0064 — continuation (respond_to) is the default reply-all path;
        // send starts a new conversation or addresses a different set.
        rendered.ShouldContain("`sv.messaging.respond_to`");
        rendered.ShouldContain("`sv.messaging.send`");
        // Nudges the runtime that stdout is reasoning trace, not delivered.
        rendered.ShouldContain("stdout text is a diagnostic reasoning trace");
    }

    // ── ADR-0066 §3: structured envelope for the A2A DataPart ─────────────

    private static InboundEnvelopeBuilder.EnvelopeMessage Envelope(
        Message inbound,
        IReadOnlyList<Address>? recipients = null,
        IReadOnlyList<Address>? participants = null,
        string? senderDisplayName = null)
    {
        var to = recipients ?? new[] { Self };
        var roster = participants ?? to.Concat(new[] { Sender }).ToList();
        return new InboundEnvelopeBuilder.EnvelopeMessage(inbound, senderDisplayName, to, roster);
    }

    [Fact]
    public void BuildEnvelopeData_CarriesSelfDescribedEnvelope_MatchingTheProseShape()
    {
        // The structured DataPart a deterministic runtime reads carries the
        // same from/to/participants/message_id/in_reply_to the prose appendix
        // does — as data, not text to re-parse.
        var answered = Guid.NewGuid();
        var inbound = new Message(
            Guid.NewGuid(), Sender, Self, MessageType.Domain,
            ThreadId: null, JsonSerializer.SerializeToElement("hello"),
            KnownTimestamp, InReplyTo: answered);

        var data = InboundEnvelopeBuilder.BuildEnvelopeData(
            new[] { Envelope(inbound, new[] { Self, Other }, new[] { Self, Other, Sender }) });

        data.ShouldContainKey("envelopes");
        data["envelopes"].ValueKind.ShouldBe(JsonValueKind.Array);
        var envelopes = data["envelopes"].EnumerateArray().ToList();
        envelopes.Count.ShouldBe(1);

        var env = envelopes[0];
        env.GetProperty("from").GetString().ShouldBe(Sender.ToString());
        env.GetProperty("message_id").GetString().ShouldBe(GuidFormatter.Format(inbound.Id));
        env.GetProperty("in_reply_to").GetString().ShouldBe(GuidFormatter.Format(answered));
        env.GetProperty("to").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { Self.ToString(), Other.ToString() });
        env.GetProperty("participants").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { Self.ToString(), Other.ToString(), Sender.ToString() });
    }

    [Fact]
    public void BuildEnvelopeData_OmitsInReplyTo_ForOriginalMessage()
    {
        var inbound = new Message(
            Guid.NewGuid(), Sender, Self, MessageType.Domain,
            ThreadId: null, JsonSerializer.SerializeToElement("hi"), KnownTimestamp);

        var data = InboundEnvelopeBuilder.BuildEnvelopeData(new[] { Envelope(inbound) });

        var env = data["envelopes"].EnumerateArray().Single();
        env.TryGetProperty("in_reply_to", out _).ShouldBeFalse();
    }

    [Fact]
    public void BuildEnvelopeData_BatchPreservesOrder()
    {
        // #3056: a batched turn carries one envelope per message, oldest-first;
        // the runtime reasons over the whole set and acts on the latest.
        var first = new Message(
            Guid.NewGuid(), Sender, Self, MessageType.Domain,
            ThreadId: null, JsonSerializer.SerializeToElement("1"), KnownTimestamp);
        var second = new Message(
            Guid.NewGuid(), Sender, Self, MessageType.Domain,
            ThreadId: null, JsonSerializer.SerializeToElement("2"), KnownTimestamp);

        var data = InboundEnvelopeBuilder.BuildEnvelopeData(
            new[] { Envelope(first), Envelope(second) });

        var envelopes = data["envelopes"].EnumerateArray().ToList();
        envelopes.Count.ShouldBe(2);
        envelopes[0].GetProperty("message_id").GetString().ShouldBe(GuidFormatter.Format(first.Id));
        envelopes[1].GetProperty("message_id").GetString().ShouldBe(GuidFormatter.Format(second.Id));
    }
}
