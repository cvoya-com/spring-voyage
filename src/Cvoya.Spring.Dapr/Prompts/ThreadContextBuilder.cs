// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Text;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Builds the thread context layer (Layer 3) from prior messages
/// and checkpoint state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sender rendering (#2129).</b> Prior turns are rendered as
/// <c>[ts] {Sender}: {text}</c>. <c>{Sender}</c> resolves to one of:
/// </para>
/// <list type="number">
///   <item><description>
///     The pre-resolved display name supplied via
///     <c>senderDisplayNames</c> when present and non-empty (built
///     upstream from
///     <see cref="Cvoya.Spring.Core.Security.IParticipantDisplayNameResolver"/>).
///   </description></item>
///   <item><description>
///     Otherwise the address's <c>Scheme</c> literal (e.g. <c>human</c>,
///     <c>agent</c>, <c>unit</c>) — NEVER the raw <c>scheme://&lt;guid&gt;</c>
///     wire form. The pre-#2129 format leaked platform-internal addressing
///     into the Layer 3 prompt, which weak LLMs observed in #2089 were
///     happy to mimic verbatim on output. Falling back to the bare scheme
///     keeps the prompt parsable while denying the model the dot-pattern
///     it was copying.
///   </description></item>
/// </list>
/// </remarks>
public class ThreadContextBuilder
{
    /// <summary>
    /// Builds the thread context string from the provided thread state.
    /// </summary>
    /// <param name="priorMessages">The prior messages in the thread.</param>
    /// <param name="lastCheckpoint">Optional last checkpoint state.</param>
    /// <param name="senderDisplayNames">
    /// Optional pre-resolved map from prior-message sender
    /// <see cref="Address"/> to a human-readable display name. Built upstream
    /// by the caller (<c>PromptAssembler</c> reads it from
    /// <see cref="Cvoya.Spring.Core.Execution.PromptAssemblyContext.PriorMessageSenderDisplayNames"/>).
    /// When <c>null</c> or missing an address, the formatter falls back to
    /// the address's <c>Scheme</c> literal — see the type-level remarks.
    /// </param>
    /// <returns>The formatted thread context string, or an empty string if all inputs are empty.</returns>
    public string Build(
        IReadOnlyList<Message> priorMessages,
        string? lastCheckpoint,
        IReadOnlyDictionary<Address, string>? senderDisplayNames = null)
    {
        var builder = new StringBuilder();

        if (priorMessages.Count > 0)
        {
            builder.AppendLine("### Prior Messages");
            foreach (var message in priorMessages)
            {
                var sender = ResolveSender(message.From, senderDisplayNames);
                var text = MessagePayloadText.Extract(message.Payload);
                builder.AppendLine($"[{message.Timestamp:u}] {sender}: {text}");
            }

            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(lastCheckpoint))
        {
            builder.AppendLine("### Last Checkpoint");
            builder.AppendLine(lastCheckpoint);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string ResolveSender(
        Address from,
        IReadOnlyDictionary<Address, string>? senderDisplayNames)
    {
        if (senderDisplayNames is not null
            && senderDisplayNames.TryGetValue(from, out var displayName)
            && !string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        // Fallback: render the bare scheme literal ("human", "agent",
        // "unit", …). Intentionally NOT the canonical wire form
        // ("scheme:<guid>") and NOT the legacy navigation form
        // ("scheme://<guid>") — both leak platform-internal addressing
        // into Layer 3, which weak LLMs were observed mimicking on
        // output (#2089 / #2129).
        return from.Scheme;
    }

}
