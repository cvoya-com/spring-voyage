// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Collections.Generic;
using System.Threading;

/// <summary>
/// In-memory per-thread dispatcher tracking shared by <see cref="AgentActor"/>
/// and <see cref="UnitActor"/> (#2491). Holds one
/// <see cref="CancellationTokenSource"/> entry per thread that currently has a
/// dispatcher running on the owning actor; both actors use the same
/// <c>IRuntimeInvocationPath</c> seam (per ADR-0039) so a single tracker
/// shape captures their in-flight bookkeeping.
/// </summary>
/// <remarks>
/// <para>
/// The tracker is non-persistent on purpose: it lives only inside the
/// actor's process memory. A Dapr actor placement change drops the
/// tracker along with the actor instance, which is the desired outcome
/// — the new placement has no inherited in-flight state.
/// </para>
/// <para>
/// Thread-safety follows the actor turn model: every mutation runs inside
/// the actor's turn queue, so callers do not need their own locking.
/// </para>
/// </remarks>
internal sealed class ActorDispatchChannelTracker
{
    private readonly Dictionary<string, CancellationTokenSource> _entries = new();

    /// <summary>
    /// Adds (or replaces) the entry for <paramref name="threadId"/> with a
    /// fresh <see cref="CancellationTokenSource"/>. Replacing an existing
    /// entry disposes the previous source so an orphaned cancel chain
    /// can't be triggered by accident.
    /// </summary>
    public CancellationTokenSource Enter(string threadId)
    {
        if (_entries.TryGetValue(threadId, out var existing))
        {
            existing.Dispose();
        }
        var cts = new CancellationTokenSource();
        _entries[threadId] = cts;
        return cts;
    }

    /// <summary>
    /// Cancels and removes the entry for <paramref name="threadId"/> if
    /// one exists. Returns <c>true</c> when an entry was found and
    /// cancelled, <c>false</c> otherwise. The signal is fire-and-forget
    /// per ADR-0030 §44 — callers cancel and move on; the dispatcher's
    /// own observation of the token is what eventually completes the
    /// pipeline.
    /// </summary>
    public async ValueTask<bool> CancelAsync(string threadId)
    {
        if (!_entries.TryGetValue(threadId, out var cts))
        {
            return false;
        }
        await cts.CancelAsync();
        cts.Dispose();
        _entries.Remove(threadId);
        return true;
    }

    /// <summary>
    /// Removes the entry for <paramref name="threadId"/> without
    /// cancelling. Used by the dispatch-exit path where the dispatcher
    /// returned of its own accord.
    /// </summary>
    public void Exit(string threadId)
    {
        if (_entries.TryGetValue(threadId, out var cts))
        {
            cts.Dispose();
            _entries.Remove(threadId);
        }
    }

    /// <summary>
    /// Returns the current entry count — the number of threads with a
    /// dispatcher currently running on this actor.
    /// </summary>
    public int InFlightCount => _entries.Count;

    /// <summary>
    /// Returns <c>true</c> when an entry exists for the supplied thread.
    /// </summary>
    public bool Contains(string threadId) => _entries.ContainsKey(threadId);

    /// <summary>
    /// Attempts to retrieve the <see cref="CancellationTokenSource"/> for
    /// <paramref name="threadId"/>. Used by callers (e.g. AgentActor's
    /// amendment-cancel path) that need direct access to the source for
    /// downstream wiring; the standard cancel + remove path should prefer
    /// <see cref="CancelAsync"/>.
    /// </summary>
    public bool TryGet(string threadId, out CancellationTokenSource cts)
    {
        return _entries.TryGetValue(threadId, out cts!);
    }
}
