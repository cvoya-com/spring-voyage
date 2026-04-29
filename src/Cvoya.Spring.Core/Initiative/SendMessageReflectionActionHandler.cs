// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Default handler for the <c>send-message</c> reflection action type —
/// the Tier-2 action the initiative engine emits when it decides the agent
/// should send a free-form message to another addressable.
/// </summary>
/// <remarks>
/// <para>
/// Expected payload shape:
/// <code>
/// {
///   "targetScheme": "agent",
///   "targetPath":   "engineering-team/ada",
///   "content":      "…free-form message body…",
///   "threadId": "optional-correlation-id"
/// }
/// </code>
/// If any required field is missing, <see cref="TranslateAsync"/> returns
/// <c>null</c> and the caller surfaces a <c>ReflectionActionSkipped</c>
/// activity event with reason <c>MalformedPayload</c>. The handler does not
/// throw for bad input — rejections must always be visible through the
/// activity-event channel.
/// </para>
/// <para>
/// Kept in <c>Cvoya.Spring.Core</c> so it has no infrastructure dependency
/// and so the private cloud repo can reuse the same translator when it
/// swaps in a tenant-aware <see cref="IReflectionActionHandlerRegistry"/>.
/// </para>
/// </remarks>
public class SendMessageReflectionActionHandler : IReflectionActionHandler
{
    /// <summary>
    /// The canonical action-type string this handler matches.
    /// </summary>
    public const string ActionTypeName = "send-message";

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

        if (!ReflectionActionPayloadHelpers.TryGetString(payload, "content", out var content) ||
            string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult<Message?>(null);
        }

        var threadId = ReflectionActionPayloadHelpers.ReadThreadId(payload);

        var bodyPayload = JsonSerializer.SerializeToElement(new { Content = content });

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