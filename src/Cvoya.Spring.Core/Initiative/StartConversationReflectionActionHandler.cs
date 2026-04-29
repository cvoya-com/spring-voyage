// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Default handler for the <c>start-conversation</c> reflection action type.
/// Mints a fresh <c>ThreadId</c> if the payload does not supply one,
/// so agents that act on their own initiative do not accidentally append to
/// an unrelated live thread.
/// </summary>
/// <remarks>
/// Expected payload shape:
/// <code>
/// {
///   "targetScheme": "agent",
///   "targetPath":   "engineering-team/ada",
///   "topic":        "Short human-readable thread topic",
///   "content":      "Optional first-message body"
/// }
/// </code>
/// <para>
/// A missing <c>content</c> falls back to the <c>topic</c> so the translated
/// message still carries a non-empty body. A missing <c>target*</c> pair
/// returns <c>null</c>.
/// </para>
/// </remarks>
public class StartConversationReflectionActionHandler : IReflectionActionHandler
{
    /// <summary>The canonical action-type string this handler matches.</summary>
    public const string ActionTypeName = "start-conversation";

    /// <inheritdoc />
    public string ActionType => ActionTypeName;

    /// <inheritdoc />
    public virtual Task<Message?> TranslateAsync(
        Address agentAddress,
        ReflectionOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentAddress);
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.ActionPayload is not { } payload ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return Task.FromResult<Message?>(null);
        }

        var target = ReflectionActionPayloadHelpers.ReadTarget(payload);
        if (target is null)
        {
            return Task.FromResult<Message?>(null);
        }

        ReflectionActionPayloadHelpers.TryGetString(payload, "topic", out var topic);
        ReflectionActionPayloadHelpers.TryGetString(payload, "content", out var content);

        var body = !string.IsNullOrWhiteSpace(content)
            ? content
            : topic;

        if (string.IsNullOrWhiteSpace(body))
        {
            return Task.FromResult<Message?>(null);
        }

        var threadId =
            ReflectionActionPayloadHelpers.ReadThreadId(payload)
            ?? Guid.NewGuid().ToString();

        var bodyPayload = JsonSerializer.SerializeToElement(new
        {
            Topic = topic,
            Content = body,
        });

        var message = new Message(
            Guid.NewGuid(),
            agentAddress,
            target,
            MessageType.Domain,
            threadId,
            bodyPayload,
            DateTimeOffset.UtcNow);

        return Task.FromResult<Message?>(message);
    }
}