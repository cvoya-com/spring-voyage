// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Globalization;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Renders the inbound-message envelope the platform delivers to a runtime's
/// user-message slot (#2746). The envelope makes the platform's view of
/// the interaction explicit at the input boundary so the runtime cannot
/// drift into "answer this chat turn as text" — every inbound delivery
/// names the sender, the participants the message was addressed to, and
/// the payload, and reminds the runtime that responding is a tool call.
/// <para>
/// Format: bullet-style header + a single fenced JSON appendix. The
/// header is human-readable; the JSON appendix is machine-parseable so a
/// structured payload (e.g. a GitHub webhook event delivered via a
/// connector) survives intact through the user-message slot.
/// </para>
/// <para>
/// The platform deliberately omits the <c>thread_id</c> from the
/// envelope (#2747). Threads are identified by their participant set —
/// the runtime sees the participants and asks for shared history via
/// <c>sv.memory.history_with(participants=[…])</c>.
/// </para>
/// <para>
/// The envelope carries both <c>to</c> (the recipients the sender targeted,
/// receiver included, sender excluded) and <c>participants</c> (the full
/// routable roster of the conversation, ADR-0064). <c>participants</c> is
/// the set <c>sv.messaging.respond_to</c> delivers to, so the runtime can
/// continue the conversation without reconstructing the roster itself.
/// </para>
/// </summary>
internal static class InboundEnvelopeBuilder
{
    /// <summary>
    /// Builds the rendered user-message envelope for one inbound delivery.
    /// </summary>
    /// <param name="inbound">The message being delivered to the runtime.</param>
    /// <param name="senderDisplayName">
    /// Resolved display name for <paramref name="inbound"/>'s sender, or
    /// <c>null</c> when the directory had no entry (typical for
    /// connectors, which stamp message provenance but aren't directory
    /// entries).
    /// </param>
    /// <param name="recipientParticipants">
    /// The non-sender participants of the thread the message was
    /// delivered on — the envelope's <c>to</c> field. For
    /// <c>sv.messaging.send</c> this is every recipient (so any one
    /// recipient sees the others); for <c>sv.messaging.multicast</c> it is
    /// just the single recipient. The runtime's own address always appears
    /// here when it is a recipient.
    /// </param>
    /// <param name="participants">
    /// The full <b>routable</b> roster of the conversation (ADR-0064):
    /// <c>to</c> together with the sender when the sender is itself routable
    /// — "everyone you could reach here". Always includes the receiving
    /// runtime; never includes a non-routable origin (a <c>connector://</c>
    /// sender appears in <c>from</c> only). This is the set
    /// <c>sv.messaging.respond_to</c> delivers to.
    /// </param>
    public static string Render(
        Message inbound,
        string? senderDisplayName,
        IReadOnlyList<Address> recipientParticipants,
        IReadOnlyList<Address> participants)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(recipientParticipants);
        ArgumentNullException.ThrowIfNull(participants);

        var sb = new StringBuilder();
        sb.AppendLine("You received a message.");
        sb.AppendLine();
        AppendMessageBlock(sb, inbound, senderDisplayName, recipientParticipants, participants);
        sb.AppendLine();
        sb.AppendLine(
            "Decide what to do. To continue this conversation with everyone here, call " +
            "`sv.messaging.respond_to` with this `message_id` and your body. To start a new " +
            "conversation or address a different set, call `sv.messaging.send` with the recipient " +
            "address(es) and body. stdout text is a diagnostic reasoning trace only — no participant sees it.");

        return sb.ToString();
    }

    /// <summary>
    /// Renders a batch of inbound messages the platform delivered to the
    /// runtime in a single turn (#3056). Every message is from the same
    /// conversation (the same participant set), listed oldest-first and each
    /// self-described — <c>from</c>, <c>to</c>, <c>participants</c>,
    /// <c>message_id</c>, <c>timestamp</c>, <c>payload</c> — so the runtime can
    /// order and attribute them, reason over the net current state, and act
    /// once. A single-element batch renders byte-for-byte identically to
    /// <see cref="Render"/> (the common, one-message-per-turn case).
    /// </summary>
    public static string RenderBatch(IReadOnlyList<EnvelopeMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            throw new ArgumentException(
                "An inbound envelope batch must contain at least one message.", nameof(messages));
        }

        if (messages.Count == 1)
        {
            var only = messages[0];
            return Render(only.Inbound, only.SenderDisplayName, only.RecipientParticipants, only.Participants);
        }

        var n = messages.Count;
        var sb = new StringBuilder();
        sb.Append("You received ").Append(n).AppendLine(" messages in this conversation, delivered together as one set.");
        sb.AppendLine();
        sb.AppendLine(
            "They are listed below in the order received (oldest first), and they are all part of " +
            "the same conversation — the same participants throughout, so there is nothing to switch " +
            "between. Read the whole set before acting: a later message may update, answer, or " +
            "supersede an earlier one, so what you do should reflect the net current state rather than " +
            "each message taken in isolation. You may handle them one by one, group related ones, or " +
            "treat the set as a whole — then take the actions the resulting state calls for, in this " +
            "one turn.");
        sb.AppendLine();
        for (var i = 0; i < n; i++)
        {
            var item = messages[i];
            sb.Append("--- message ").Append(i + 1).Append(" of ").Append(n).AppendLine(" ---");
            AppendMessageBlock(sb, item.Inbound, item.SenderDisplayName, item.RecipientParticipants, item.Participants);
            sb.AppendLine();
        }

        sb.AppendLine(
            "Decide what to do across the whole set. To continue this conversation with everyone here, " +
            "call `sv.messaging.respond_to` with the `message_id` of the message you are addressing and " +
            "your body. To start a new conversation or address a different set, call `sv.messaging.send` " +
            "with the recipient address(es) and body. stdout text is a diagnostic reasoning trace only — " +
            "no participant sees it.");

        return sb.ToString();
    }

    /// <summary>
    /// Appends one self-described message block — the bullet header plus the
    /// fenced JSON appendix — to <paramref name="sb"/>. Shared by the single
    /// (<see cref="Render"/>) and batched (<see cref="RenderBatch"/>) renders so
    /// each message carries the identical, complete set of fields whether it is
    /// delivered alone or as part of a set.
    /// </summary>
    private static void AppendMessageBlock(
        StringBuilder sb,
        Message inbound,
        string? senderDisplayName,
        IReadOnlyList<Address> recipientParticipants,
        IReadOnlyList<Address> participants)
    {
        var payloadText = MessagePayloadText.Extract(inbound.Payload);
        var fromRendered = string.IsNullOrWhiteSpace(senderDisplayName)
            ? inbound.From.ToString()
            : $"{inbound.From} ({senderDisplayName})";

        sb.Append("- from: ").AppendLine(fromRendered);
        sb.Append("- to: [")
            .Append(string.Join(", ", recipientParticipants.Select(a => a.ToString())))
            .AppendLine("]");
        sb.Append("- participants: [")
            .Append(string.Join(", ", participants.Select(a => a.ToString())))
            .AppendLine("]");
        sb.Append("- message_id: ").AppendLine(GuidFormatter.Format(inbound.Id));
        if (inbound.InReplyTo is { } headerInReplyTo)
        {
            sb.Append("- in_reply_to: ").AppendLine(GuidFormatter.Format(headerInReplyTo));
        }
        sb.Append("- timestamp: ").AppendLine(
            inbound.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        sb.AppendLine("- payload:");
        sb.AppendLine();
        sb.AppendLine(RenderPayloadForHeader(payloadText, inbound.Payload));
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(RenderEnvelopeJson(inbound, senderDisplayName, recipientParticipants, participants));
        sb.AppendLine("```");
    }

    /// <summary>
    /// One message's fully-resolved envelope inputs — the inbound message plus
    /// the directory / participant-set projections the builder needs. Bundled
    /// so <see cref="RenderBatch"/> can take a list without parallel arrays.
    /// </summary>
    /// <param name="Inbound">The message being delivered.</param>
    /// <param name="SenderDisplayName">
    /// Resolved display name for the sender, or <c>null</c> when the directory
    /// had no entry (typical for connectors).
    /// </param>
    /// <param name="RecipientParticipants">The envelope's <c>to</c> field (receiver included, sender excluded).</param>
    /// <param name="Participants">The full routable roster (ADR-0064) — the <c>respond_to</c> delivery set.</param>
    public readonly record struct EnvelopeMessage(
        Message Inbound,
        string? SenderDisplayName,
        IReadOnlyList<Address> RecipientParticipants,
        IReadOnlyList<Address> Participants);

    /// <summary>
    /// Renders the payload for the bullet header. Free-text payloads appear
    /// inline so the model can read them at a glance; structured payloads
    /// (those that survived the <c>MessagePayloadText.Extract</c> object
    /// fallback) are surfaced as <c>&lt;structured payload — see JSON
    /// appendix&gt;</c> so the header stays scannable and the runtime is
    /// nudged toward the fenced block.
    /// </summary>
    private static string RenderPayloadForHeader(string extracted, JsonElement payload)
    {
        // MessagePayloadText.Extract returns the raw object JSON when the
        // payload was object-shaped but carried neither `text` nor `Task`.
        // For those (connector webhook events, custom payloads), point
        // the model at the JSON appendix instead of dumping it twice.
        if (payload.ValueKind == JsonValueKind.Object
            && !payload.TryGetProperty("text", out _)
            && !payload.TryGetProperty("Task", out _))
        {
            return "<structured payload — see JSON appendix>";
        }
        return string.IsNullOrEmpty(extracted) ? "<empty>" : extracted;
    }

    private static string RenderEnvelopeJson(
        Message inbound,
        string? senderDisplayName,
        IReadOnlyList<Address> recipientParticipants,
        IReadOnlyList<Address> participants)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            WriteEnvelopeObject(writer, inbound, senderDisplayName, recipientParticipants, participants);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes one self-described envelope object (<c>from</c>, <c>to</c>,
    /// <c>participants</c>, <c>message_id</c>, <c>in_reply_to</c>,
    /// <c>timestamp</c>, <c>payload</c>) to <paramref name="writer"/>. Shared by
    /// the rendered-prose JSON appendix (<see cref="RenderEnvelopeJson"/>) and
    /// the structured A2A data part (<see cref="BuildEnvelopeData"/>, ADR-0066
    /// §3) so both carry the identical, complete shape.
    /// </summary>
    private static void WriteEnvelopeObject(
        Utf8JsonWriter writer,
        Message inbound,
        string? senderDisplayName,
        IReadOnlyList<Address> recipientParticipants,
        IReadOnlyList<Address> participants)
    {
        writer.WriteStartObject();
        writer.WriteString("from", inbound.From.ToString());
        if (!string.IsNullOrWhiteSpace(senderDisplayName))
        {
            writer.WriteString("from_display_name", senderDisplayName);
        }
        writer.WritePropertyName("to");
        writer.WriteStartArray();
        foreach (var recipient in recipientParticipants)
        {
            writer.WriteStringValue(recipient.ToString());
        }
        writer.WriteEndArray();
        writer.WritePropertyName("participants");
        writer.WriteStartArray();
        foreach (var participant in participants)
        {
            writer.WriteStringValue(participant.ToString());
        }
        writer.WriteEndArray();
        writer.WriteString("message_id", GuidFormatter.Format(inbound.Id));
        // ADR-0066 §5: when this message is a reply, name the message it
        // answers so a sender can correlate fan-out replies without an
        // echoed token.
        if (inbound.InReplyTo is { } inReplyTo)
        {
            writer.WriteString("in_reply_to", GuidFormatter.Format(inReplyTo));
        }
        writer.WriteString("timestamp", inbound.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        writer.WritePropertyName("payload");
        inbound.Payload.WriteTo(writer);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Builds the structured envelope for the inbound A2A <c>DataPart</c>
    /// (ADR-0066 §3): a <c>{ "envelopes": [ … ] }</c> map carrying one
    /// self-described envelope per message, in the same order and shape as the
    /// rendered-prose appendix. Deterministic runtimes read this directly
    /// instead of re-parsing the prose; LLM runtimes keep reading the text part.
    /// The return value is the <c>DataPart.Data</c> dictionary.
    /// </summary>
    public static Dictionary<string, JsonElement> BuildEnvelopeData(
        IReadOnlyList<EnvelopeMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var m in messages)
            {
                WriteEnvelopeObject(
                    writer, m.Inbound, m.SenderDisplayName, m.RecipientParticipants, m.Participants);
            }
            writer.WriteEndArray();
        }
        var envelopes = JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["envelopes"] = envelopes,
        };
    }
}

/// <summary>
/// The rendered inbound envelope the dispatcher delivers: the prose
/// <see cref="Text"/> (the user-message slot every runtime reads) plus the
/// structured <see cref="Data"/> for the A2A <c>DataPart</c> (ADR-0066 §3), so
/// a deterministic runtime reads the envelope as data rather than re-parsing
/// the prose. <see cref="Data"/> is <c>null</c> only when a resolver does not
/// produce structured data (e.g. a test stub).
/// </summary>
public readonly record struct RenderedInboundEnvelope(
    string Text,
    Dictionary<string, JsonElement>? Data);

/// <summary>
/// Resolves the directory metadata and thread-participant set the
/// envelope builder needs. Pulls <see cref="IDirectoryService"/> and
/// <see cref="IThreadRegistry"/> so the dispatcher only needs to ask for
/// one piece of pre-resolved data. <c>public</c> so the dispatcher
/// (which itself is <c>public</c>) can declare a constructor parameter
/// of this type; cloud overlays may also swap the implementation via DI.
/// </summary>
public interface IInboundEnvelopeResolver
{
    /// <summary>
    /// Renders the user-message envelope for a single inbound delivery.
    /// Convenience over <see cref="RenderEnvelopeAsync(IReadOnlyList{Message}, CancellationToken)"/>
    /// for the one-message-per-turn case.
    /// </summary>
    Task<RenderedInboundEnvelope> RenderEnvelopeAsync(Message inbound, CancellationToken cancellationToken);

    /// <summary>
    /// Renders the user-message envelope for a batch of inbound messages
    /// delivered to the runtime in a single turn (#3056). The batch is
    /// oldest-first and shares one conversation; each message is rendered
    /// self-described so the runtime can order and attribute them. A
    /// single-element batch renders identically to the single overload.
    /// </summary>
    Task<RenderedInboundEnvelope> RenderEnvelopeAsync(IReadOnlyList<Message> batch, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IInboundEnvelopeResolver"/>: resolves the sender's
/// display name via <see cref="IDirectoryService"/> (gracefully falling
/// back to <c>null</c> when the sender has no directory row — typical for
/// connectors) and the thread participant set from
/// <see cref="IThreadRegistry"/>. Derives both the <c>to</c> field (the
/// thread participants minus the sender) and the routable
/// <c>participants</c> roster (ADR-0064). Both lookups happen inside a
/// fresh DI scope per call so the resolver itself can live as a singleton
/// alongside the dispatcher.
/// </summary>
public sealed class InboundEnvelopeResolver(
    IServiceScopeFactory scopeFactory) : IInboundEnvelopeResolver
{
    public Task<RenderedInboundEnvelope> RenderEnvelopeAsync(
        Message inbound, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        return RenderEnvelopeAsync([inbound], cancellationToken);
    }

    public async Task<RenderedInboundEnvelope> RenderEnvelopeAsync(
        IReadOnlyList<Message> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            throw new ArgumentException(
                "An inbound envelope batch must contain at least one message.", nameof(batch));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var directoryService = scope.ServiceProvider.GetRequiredService<IDirectoryService>();
        var threadRegistry = scope.ServiceProvider.GetRequiredService<IThreadRegistry>();

        // Resolve each message's envelope inputs. Sender display names are
        // cached within the batch — a thread typically batches several
        // messages from the same sender, so this collapses N directory hits to
        // one per distinct sender. The participant projection is resolved per
        // message because `to` excludes that message's own sender (which can
        // differ across a multi-sender conversation).
        var displayNameCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var items = new List<InboundEnvelopeBuilder.EnvelopeMessage>(batch.Count);
        foreach (var inbound in batch)
        {
            var fromKey = inbound.From.ToString();
            if (!displayNameCache.TryGetValue(fromKey, out var displayName))
            {
                var senderEntry = await directoryService.ResolveAsync(inbound.From, cancellationToken);
                displayName = senderEntry?.DisplayName;
                displayNameCache[fromKey] = displayName;
            }

            var (recipientParticipants, participants) = await ResolveParticipantsAsync(
                threadRegistry, inbound, cancellationToken);

            items.Add(new InboundEnvelopeBuilder.EnvelopeMessage(
                inbound, displayName, recipientParticipants, participants));
        }

        // Render once, surface both shapes: the prose Text every runtime reads,
        // and the structured Data for the A2A DataPart (ADR-0066 §3).
        var text = InboundEnvelopeBuilder.RenderBatch(items);
        var data = InboundEnvelopeBuilder.BuildEnvelopeData(items);
        return new RenderedInboundEnvelope(text, data);
    }

    /// <summary>
    /// Resolves the two participant projections the envelope needs from the
    /// thread's persisted participant set (#2596 / ADR-0030):
    /// <list type="bullet">
    ///   <item><description><c>to</c> — the thread participants minus the
    ///   sender: the recipients the original sender targeted, the receiver
    ///   among them.</description></item>
    ///   <item><description><c>participants</c> — the full <b>routable</b>
    ///   roster (ADR-0064): every routable thread member (agent / unit /
    ///   human), so the receiver and the routable sender are both present;
    ///   a non-routable origin (a <c>connector://</c> sender) is excluded —
    ///   it appears in <c>from</c> only.</description></item>
    /// </list>
    /// When the thread can't be resolved (race during create, mismatched id)
    /// both fall back to this hop's addresses so the envelope is still
    /// useful.
    /// </summary>
    private static async Task<(IReadOnlyList<Address> To, IReadOnlyList<Address> Participants)>
        ResolveParticipantsAsync(
            IThreadRegistry threadRegistry, Message inbound, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(inbound.ThreadId))
        {
            var entry = await threadRegistry.ResolveAsync(inbound.ThreadId, ct);
            if (entry is { Participants.Count: > 0 })
            {
                var senderKey = inbound.From.ToString();
                var to = entry.Participants
                    .Where(a => !string.Equals(a.ToString(), senderKey, StringComparison.Ordinal))
                    .ToList();
                if (to.Count > 0)
                {
                    var participants = entry.Participants
                        .Where(a => a.IsRoutable)
                        .ToList();
                    return (to, participants);
                }
            }
        }

        // Thread unresolved: fall back to this hop's recipient for `to`, and
        // the routable parties of this hop for `participants`.
        var fallbackTo = new[] { inbound.To };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fallbackParticipants = new[] { inbound.To, inbound.From }
            .Where(a => a.IsRoutable && seen.Add(a.ToString()))
            .ToList();
        return (fallbackTo, fallbackParticipants);
    }
}
