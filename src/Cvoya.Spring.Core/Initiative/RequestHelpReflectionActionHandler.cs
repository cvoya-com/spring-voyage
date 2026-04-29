// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Default handler for the <c>request-help</c> reflection action type. Shapes
/// the outbound message to match the existing <c>requestHelp</c> platform
/// tool so observers that already understand that tool's payload layout can
/// handle initiative-sourced requests without special-casing.
/// </summary>
/// <remarks>
/// Expected payload shape:
/// <code>
/// {
///   "targetScheme": "agent",
///   "targetPath":   "engineering-team/ada",
///   "reason":       "Short description of what help is needed",
///   "threadId": "optional-correlation-id"
/// }
/// </code>
/// <para>
/// A missing <c>reason</c> falls through to an empty string so the router
/// still sees a well-formed message; a missing <c>target*</c> pair skips.
/// </para>
/// </remarks>
public class RequestHelpReflectionActionHandler : IReflectionActionHandler
{
    /// <summary>The canonical action-type string this handler matches.</summary>
    public const string ActionTypeName = "request-help";

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

        ReflectionActionPayloadHelpers.TryGetString(payload, "reason", out var reason);

        var bodyPayload = JsonSerializer.SerializeToElement(new
        {
            RequestHelp = true,
            Reason = reason ?? string.Empty,
        });

        var threadId = ReflectionActionPayloadHelpers.ReadThreadId(payload);

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