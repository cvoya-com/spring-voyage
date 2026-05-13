// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Text.Json;

/// <summary>
/// Single source of truth for extracting the human-facing text from a
/// <c>Message.Payload</c> <see cref="JsonElement"/>. Three shapes flow
/// through the platform and all three must map to the same string:
/// <list type="bullet">
///   <item><description>Bare JSON string (CLI / API send path).</description></item>
///   <item><description><c>{ "text": "…" }</c> object (agent-turn wrappers).</description></item>
///   <item><description><c>{ "Task": "…" }</c> object (legacy task wrapper).</description></item>
/// </list>
/// Used by <see cref="ThreadContextBuilder"/> when rendering prior turns
/// and by the A2A dispatcher when populating the user role's text part —
/// keeping them in sync prevents a regression where the dispatcher leaks
/// the assembled system prompt into the user slot for bare-string payloads
/// (#2230).
/// </summary>
internal static class MessagePayloadText
{
    /// <summary>
    /// Returns the human-facing text for <paramref name="payload"/>, or
    /// <see cref="string.Empty"/> when the payload is null/undefined.
    /// Object payloads that carry neither <c>text</c> nor <c>Task</c>
    /// fall through to the JSON serialisation so callers always get a
    /// non-null string they can hand to a downstream component.
    /// </summary>
    public static string Extract(JsonElement payload)
    {
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
