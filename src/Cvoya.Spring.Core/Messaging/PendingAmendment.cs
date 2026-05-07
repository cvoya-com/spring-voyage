// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// State-store representation of an <see cref="AmendmentPayload"/> that has
/// been queued on a live agent, waiting to be surfaced to the dispatcher
/// between tool calls or at the next model-call boundary. See #142.
/// </summary>
/// <param name="Id">The amendment message id (<see cref="Message.Id"/>), stable across retries.</param>
/// <param name="From">The supervisor that authored the amendment.</param>
/// <param name="Text">The free-form instruction body.</param>
/// <param name="Priority">How urgently the amendment must be honoured.</param>
/// <param name="CorrelationId">Optional correlation id — typically the active conversation id.</param>
/// <param name="ReceivedAt">UTC timestamp when the amendment was accepted by the recipient.</param>
public record PendingAmendment(
    Guid Id,
    Address From,
    string Text,
    AmendmentPriority Priority,
    string? CorrelationId,
    DateTimeOffset ReceivedAt);