// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Builds the conversation context layer (Layer 3) from prior messages
/// and checkpoint state.
/// </summary>
public class ConversationContextBuilder
{
    /// <summary>
    /// Builds the conversation context string from the provided conversation state.
    /// </summary>
    /// <param name="priorMessages">The prior messages in the conversation.</param>
    /// <param name="lastCheckpoint">Optional last checkpoint state.</param>
    /// <returns>The formatted conversation context string, or an empty string if all inputs are empty.</returns>
    public string Build(IReadOnlyList<Message> priorMessages, string? lastCheckpoint)
    {
        var builder = new StringBuilder();

        if (priorMessages.Count > 0)
        {
            builder.AppendLine("### Prior Messages");
            foreach (var message in priorMessages)
            {
                var sender = $"{message.From.Scheme}://{message.From.Path}";
                var text = ExtractText(message.Payload);
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

    private static string ExtractText(JsonElement payload)
    {
        // Payloads from the CLI `message send` path are serialised as a bare
        // JSON string (UntypedString on the wire); the agent-turn path wraps
        // them in { text: "..." } or { Task: "..." }. TryGetProperty throws
        // InvalidOperationException on anything that isn't an Object, so
        // guard explicitly and fall through to ToString() for primitives.
        switch (payload.ValueKind)
        {
            case JsonValueKind.Object:
                if (payload.TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String)
                {
                    return textElement.GetString() ?? string.Empty;
                }

                if (payload.TryGetProperty("Task", out var taskElement) &&
                    taskElement.ValueKind == JsonValueKind.String)
                {
                    return taskElement.GetString() ?? string.Empty;
                }

                return payload.ToString();

            case JsonValueKind.String:
                return payload.GetString() ?? string.Empty;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return string.Empty;

            default:
                return payload.ToString();
        }
    }
}