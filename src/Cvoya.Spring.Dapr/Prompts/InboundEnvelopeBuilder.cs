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

        var payloadText = MessagePayloadText.Extract(inbound.Payload);
        var fromRendered = string.IsNullOrWhiteSpace(senderDisplayName)
            ? inbound.From.ToString()
            : $"{inbound.From} ({senderDisplayName})";

        var sb = new StringBuilder();
        sb.AppendLine("You received a message.");
        sb.AppendLine();
        sb.Append("- from: ").AppendLine(fromRendered);
        sb.Append("- to: [")
            .Append(string.Join(", ", recipientParticipants.Select(a => a.ToString())))
            .AppendLine("]");
        sb.Append("- participants: [")
            .Append(string.Join(", ", participants.Select(a => a.ToString())))
            .AppendLine("]");
        sb.Append("- message_id: ").AppendLine(GuidFormatter.Format(inbound.Id));
        sb.Append("- timestamp: ").AppendLine(
            inbound.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        sb.AppendLine("- payload:");
        sb.AppendLine();
        sb.AppendLine(RenderPayloadForHeader(payloadText, inbound.Payload));
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(RenderEnvelopeJson(inbound, senderDisplayName, recipientParticipants, participants));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine(
            "Decide what to do. To continue this conversation with everyone here, call " +
            "`sv.messaging.respond_to` with this `message_id` and your body. To start a new " +
            "conversation or address a different set, call `sv.messaging.send` with the recipient " +
            "address(es) and body. stdout text is a diagnostic reasoning trace only — no participant sees it.");

        return sb.ToString();
    }

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
            writer.WriteString("timestamp", inbound.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            writer.WritePropertyName("payload");
            inbound.Payload.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

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
    Task<string> RenderEnvelopeAsync(Message inbound, CancellationToken cancellationToken);
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
    public async Task<string> RenderEnvelopeAsync(
        Message inbound, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbound);

        await using var scope = scopeFactory.CreateAsyncScope();
        var directoryService = scope.ServiceProvider.GetRequiredService<IDirectoryService>();
        var threadRegistry = scope.ServiceProvider.GetRequiredService<IThreadRegistry>();

        var senderEntry = await directoryService
            .ResolveAsync(inbound.From, cancellationToken);

        var (recipientParticipants, participants) = await ResolveParticipantsAsync(
            threadRegistry, inbound, cancellationToken);

        return InboundEnvelopeBuilder.Render(
            inbound,
            senderEntry?.DisplayName,
            recipientParticipants,
            participants);
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
