// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Threads;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Persists a domain message to the EF-authoritative <c>messages</c> table at
/// dispatch time. Implements the Thread Timeline write contract from
/// <see href="../../../docs/decisions/0030-thread-model.md">ADR-0030</see> and
/// the EF-authoritative ownership decision in
/// <see href="../../../docs/decisions/0040-actor-state-ownership-matrix.md">ADR-0040</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it sits.</b> Called by <see cref="Cvoya.Spring.Dapr.Routing.MessageRouter"/>
/// after permission / address checks and before delivery, in the same logical
/// transaction as the parent thread's <c>last_activity_at</c> bump. Message
/// rows are EF-authoritative — readers (#2054) join through
/// <c>(tenant_id, thread_id)</c> instead of scanning
/// <c>activity_events.Details</c>.
/// </para>
/// <para>
/// <b>Idempotency.</b> The implementation is a no-op when a row already
/// exists for the supplied <see cref="Message.Id"/>. Re-dispatch from a
/// retry path therefore does not duplicate history. Writes that race on the
/// same message id converge on the first inserter via the primary-key
/// uniqueness constraint.
/// </para>
/// <para>
/// <b>Scope.</b> Internal to the Dapr module. Not exposed on
/// <c>Cvoya.Spring.Core</c> because the cloud overlay does not need to swap
/// the EF write path — only the read seams (#2054) are extension points.
/// </para>
/// </remarks>
public interface IMessageWriter
{
    /// <summary>
    /// Persists <paramref name="message"/> to the <c>messages</c> table and
    /// bumps <c>threads.last_activity_at</c> for the parent thread, atomically
    /// from the caller's point of view (single
    /// <see cref="Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(System.Threading.CancellationToken)"/>
    /// boundary). No-op when the message id is already persisted.
    /// </summary>
    /// <param name="message">
    /// The dispatched message. Must be a Domain message with a non-null,
    /// Guid-shaped <see cref="Message.ThreadId"/>; control / non-Domain
    /// messages and messages without a thread id are silently skipped (the
    /// dispatcher consults <see cref="ShouldWrite"/> before invoking).
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task WriteAsync(Message message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="message"/> is in scope for
    /// persistence — i.e. a Domain message with a Guid-shaped thread id.
    /// Exposed so the dispatcher can short-circuit before opening a DI scope.
    /// </summary>
    static bool ShouldWrite(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.Type == MessageType.Domain
            && !string.IsNullOrWhiteSpace(message.ThreadId)
            && Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(message.ThreadId!, out _);
    }
}
