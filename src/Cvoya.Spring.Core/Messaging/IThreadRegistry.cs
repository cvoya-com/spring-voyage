// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Maps a participant set to a stable thread identifier and back. Implements the
/// participant-set identity decision from
/// <see href="../../../docs/decisions/0030-thread-model.md">ADR-0030</see>: a
/// thread is uniquely identified by the set of two-or-more participants
/// involved, and the same participant set always resolves to the same thread.
///
/// <para>
/// Adding or removing a participant produces a different set, hence a
/// different thread. Participant order does not matter — the registry
/// canonicalises the input before comparing.
/// </para>
///
/// <para>
/// The registry is the authoritative home for thread identity (per
/// <see href="../../../docs/decisions/0040-actor-state-ownership-matrix.md">ADR-0040</see>);
/// activity events carry the resolved id on their <c>CorrelationId</c> so
/// observability surfaces can stitch the timeline.
/// </para>
/// </summary>
public interface IThreadRegistry
{
    /// <summary>
    /// Returns the stable thread id for the given participant set, creating a
    /// new registry row if none exists. The returned id is the canonical no-dash
    /// 32-char hex form (per <c>GuidFormatter.Format</c>).
    /// </summary>
    /// <param name="participants">
    /// The set of participants. Must contain at least one element. Order is
    /// irrelevant; duplicates are collapsed.
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<string> GetOrCreateAsync(
        IEnumerable<Address> participants,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a thread id to its registry entry, or <c>null</c> when no row
    /// exists for the id. The thread id may be supplied in either the canonical
    /// no-dash form or any other shape <see cref="Guid.TryParse"/> accepts;
    /// non-Guid inputs return <c>null</c>.
    /// </summary>
    /// <param name="threadId">The thread id to resolve.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<ThreadRegistryEntry?> ResolveAsync(
        string threadId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only projection of a thread row in the registry.
/// </summary>
/// <param name="ThreadId">
/// Stable thread id in canonical no-dash 32-char hex form.
/// </param>
/// <param name="Participants">
/// The participant set as it was canonicalised on first insert. Order is the
/// canonical sort order — useful for deterministic display, not authoritative
/// for comparison.
/// </param>
/// <param name="CreatedAt">
/// Timestamp the registry row was first inserted.
/// </param>
public sealed record ThreadRegistryEntry(
    string ThreadId,
    IReadOnlyList<Address> Participants,
    DateTimeOffset CreatedAt);
