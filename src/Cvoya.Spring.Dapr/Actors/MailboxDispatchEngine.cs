// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Collections.Generic;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;

using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

/// <summary>
/// The per-actor-instance slice of an addressable subject that an actor
/// exposes to its <see cref="MailboxDispatchEngine"/> (#3031). The engine
/// owns the per-thread mailbox algorithm (enqueue / drain / dispatch / status)
/// — which is identical for <see cref="AgentActor"/> and <see cref="UnitActor"/>
/// — and calls back through this narrow facade for the handful of operations
/// that genuinely differ between the two subjects: state access, lifecycle
/// status, effective-metadata + unit-policy resolution, the runtime-invocation
/// shape (an agent builds a rich prompt context; a unit uses the lean
/// overload), the typed self-proxy hop, and activity emission.
/// </summary>
/// <remarks>
/// Composition over inheritance: rather than a shared base class, both actors
/// <c>own</c> an engine and pass <c>this</c> as the host. The engine depends
/// only on this interface, so it is unit-testable without a Dapr actor and the
/// two actors keep their (large, disjoint) domain surfaces out of any shared
/// hierarchy. ADR-0039 §"units are agents": both subjects sit on the same
/// <see cref="IRuntimeInvocationPath"/> seam, so a single mailbox engine serves
/// both.
/// </remarks>
internal interface IMailboxHost
{
    /// <summary>The owning actor's id (used for log/event correlation and config lookups).</summary>
    string ActorId { get; }

    /// <summary>The owning actor's Dapr state manager (per-thread channel persistence).</summary>
    IActorStateManager StateManager { get; }

    /// <summary>Reads the subject's authoritative lifecycle status (agents and units use distinct state keys).</summary>
    Task<LifecycleStatus> GetLifecycleStatusAsync(CancellationToken ct);

    /// <summary>
    /// Resolves the effective per-turn metadata for an inbound message. An
    /// agent resolves its membership-scoped metadata; a unit has none, so it
    /// returns a minimal <c>Enabled = true</c> record.
    /// </summary>
    Task<AgentMetadata> ResolveEffectiveMetadataAsync(Message message, CancellationToken ct);

    /// <summary>
    /// Applies unit-level policy dimensions to the effective metadata (agents
    /// only; units pass through). A non-null verdict refuses the dispatch.
    /// </summary>
    Task<(AgentMetadata Effective, PolicyVerdict? Verdict)> ApplyUnitPoliciesAsync(
        AgentMetadata effective, CancellationToken ct);

    /// <summary>
    /// Invokes the subject's runtime for the in-flight <paramref name="batch"/>
    /// of pending messages — one runtime turn for the whole set (#3056). The
    /// batch is the channel's bounded FIFO prefix, ordered oldest-first;
    /// <c>batch[0]</c> is the representative used for routing / correlation.
    /// Delivering the pending set together (rather than one message per turn)
    /// lets the runtime reason over the net current state and act once instead
    /// of responding to a stale prefix while newer messages wait. The engine
    /// supplies the per-thread <paramref name="onDispatchExit"/> callback the
    /// runtime pipeline must invoke when the dispatcher returns, so the engine
    /// can drain the batch and advance the queue. Agents build a rich
    /// <c>PromptAssemblyContext</c> from <paramref name="effective"/>; units
    /// use the lean overload and ignore it.
    /// </summary>
    Task InvokeRuntimeAsync(
        IReadOnlyList<Message> batch,
        AgentMetadata effective,
        Func<string, Task> onDispatchExit,
        CancellationToken ct);

    /// <summary>
    /// Self-invokes the owning actor's <c>OnDispatchExitAsync</c> through Dapr
    /// remoting (typed to the subject's actor interface) so the drain runs on
    /// an actor turn. Falls back to a direct call when no real proxy is
    /// available (test substitutes).
    /// </summary>
    Task SignalDispatchExitAsync(string threadId, string reason);

    /// <summary>Publishes an activity event on behalf of the owning actor.</summary>
    Task EmitActivityAsync(ActivityEvent activityEvent, CancellationToken ct);
}

/// <summary>
/// The per-thread mailbox + dispatch + drain engine shared by
/// <see cref="AgentActor"/> and <see cref="UnitActor"/> (#3031 / #2076 /
/// ADR-0030 §3 §44). One instance per actor instance; it owns the in-memory
/// per-thread dispatcher tracker, the <c>concurrent_threads</c> cache + the
/// subject-wide serialisation lock, and the per-thread channel state. Inbound
/// routing (create / append / restart) is delegated to the stateless
/// <see cref="IAgentMailboxCoordinator"/>; everything subject-specific flows
/// through <see cref="IMailboxHost"/>.
/// </summary>
internal sealed class MailboxDispatchEngine(
    IMailboxHost host,
    IAgentMailboxCoordinator coordinator,
    IAgentDefinitionProvider? definitionProvider,
    ILogger logger)
{
    private readonly ActorDispatchChannelTracker _activeWorkByThread = new();
    private SemaphoreSlim? _wideLock;
    private bool? _concurrentThreads;

    /// <summary>
    /// Upper bound on the number of pending messages delivered to the runtime
    /// in a single batched turn (#3056). On activation the engine drains a
    /// FIFO prefix of at most this many messages; any beyond it stay queued
    /// and form the next batch when the turn returns. The bound keeps a turn's
    /// context manageable when a thread has accumulated an unusually large
    /// backlog (rare) — the common case is a handful of messages that piled up
    /// while the previous turn ran.
    /// </summary>
    internal const int MaxBatchSize = 50;

    /// <summary>
    /// Exposed for tests: the currently running dispatch task (if any). Under
    /// concurrent threads multiple dispatchers may be in flight; this tracks
    /// the most-recently launched one.
    /// </summary>
    internal Task? PendingDispatchTask { get; private set; }

    /// <summary>
    /// Routes an inbound domain message into its per-thread channel via the
    /// mailbox coordinator, launching a background dispatcher when the thread
    /// has none. Returns as soon as the message is enqueued — the actor turn
    /// (and inbound delivery) never blocks on the runtime.
    /// </summary>
    /// <remarks>
    /// When the coordinator launches a dispatcher (a brand-new channel, or an
    /// idle channel restarting), the engine drains a bounded FIFO prefix of the
    /// channel's queued messages and delivers them as a single batched turn
    /// (#3056) rather than one message at a time. It records the prefix length
    /// in <see cref="ThreadChannel.InFlightCount"/> and persists it before
    /// dispatch so <see cref="DrainAsync"/> — which runs on a later actor turn —
    /// removes exactly the dispatched batch.
    /// </remarks>
    public async Task HandleInboundAsync(Message message, CancellationToken ct)
    {
        var effective = await host.ResolveEffectiveMetadataAsync(message, ct);
        var lifecycleStatus = await host.GetLifecycleStatusAsync(ct);

        await coordinator.HandleDomainMessageAsync(
            agentId: host.ActorId,
            message: message,
            effective: effective,
            lifecycleStatus: lifecycleStatus,
            applyUnitPolicies: host.ApplyUnitPoliciesAsync,
            getChannel: GetChannelAsync,
            saveChannel: SaveChannelAsync,
            dispatch: async (channel, eff, _) =>
            {
                // #3056: deliver the channel's pending FIFO prefix as one batch.
                // Record the in-flight count and persist it before launching so
                // the drain (a separate actor turn) removes exactly this batch.
                var batch = TakeBatch(channel.Messages);
                channel.InFlightCount = batch.Count;
                await SaveChannelAsync(channel, ct);

                var cts = _activeWorkByThread.Enter(channel.ThreadId);
                PendingDispatchTask = DispatchAsync(batch, eff, cts.Token);
            },
            emitActivity: host.EmitActivityAsync,
            cancellationToken: ct);
    }

    /// <summary>
    /// Snapshots the FIFO prefix of <paramref name="messages"/> that forms the
    /// next batched turn — at most <see cref="MaxBatchSize"/> messages, in
    /// arrival order (oldest first). Returns a copy so the dispatched batch is
    /// stable even as new messages append to the live channel during the turn.
    /// </summary>
    private static IReadOnlyList<Message> TakeBatch(IReadOnlyList<Message> messages)
    {
        var count = Math.Min(messages.Count, MaxBatchSize);
        var batch = new List<Message>(count);
        for (var i = 0; i < count; i++)
        {
            batch.Add(messages[i]);
        }
        return batch;
    }

    /// <summary>
    /// Advances the per-thread queue when a dispatcher returns (the actor's
    /// <c>OnDispatchExitAsync</c> calls this on a turn). Removes the whole
    /// in-flight batch (#3056) — the leading <see cref="ThreadChannel.InFlightCount"/>
    /// messages — in one atomic state write; on an authoritative stop, drops
    /// the remaining backlog and removes the channel; when the queue is empty,
    /// removes the channel; otherwise re-arms the dispatcher with the next
    /// bounded batch.
    /// </summary>
    public async Task DrainAsync(string threadId, string? reason, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(threadId))
        {
            logger.LogWarning(
                "Actor {ActorId} OnDispatchExit called without a thread id (reason: {Reason}).",
                host.ActorId, reason);
            return;
        }

        var channel = await GetChannelAsync(threadId, ct);
        if (channel is null)
        {
            // Channel was already cleared (e.g. by a per-thread cancel).
            _activeWorkByThread.Exit(threadId);
            logger.LogDebug(
                "Actor {ActorId} OnDispatchExit no-op for thread {ThreadId} (reason: {Reason}).",
                host.ActorId, threadId, reason);
            return;
        }

        // #3056: remove the whole dispatched batch — the leading InFlightCount
        // messages — in one step. Messages that arrived during the turn were
        // appended at the tail (behind the batch), so removing the leading
        // slice preserves per-thread FIFO for the remaining queue. A
        // pre-#3056 channel (or a defensive InFlightCount of 0) removes the
        // single head, matching the one-at-a-time behaviour this replaced.
        var processed = channel.InFlightCount > 0
            ? Math.Min(channel.InFlightCount, channel.Messages.Count)
            : Math.Min(1, channel.Messages.Count);
        if (processed > 0)
        {
            channel.Messages.RemoveRange(0, processed);
        }
        channel.InFlightCount = 0;

        _activeWorkByThread.Exit(threadId);

        // #2981: if the subject was stopped while this dispatcher was running,
        // quiesce — drop the remaining queue (authoritative stop = drop, not
        // hold) rather than re-arming the drain loop.
        var lifecycleStatus = await host.GetLifecycleStatusAsync(ct);
        if (lifecycleStatus.IsHalted())
        {
            await RemoveChannelAsync(threadId, ct);
            logger.LogInformation(
                "Actor {ActorId} thread {ThreadId} drain quiesced: lifecycle status is {Status} (reason: {Reason})",
                host.ActorId, threadId, lifecycleStatus, reason);
            return;
        }

        if (channel.Messages.Count == 0)
        {
            // Drain complete — remove the channel so a subsequent inbound on
            // the same thread starts fresh.
            await RemoveChannelAsync(threadId, ct);
            logger.LogInformation(
                "Actor {ActorId} thread {ThreadId} drain complete (reason: {Reason})",
                host.ActorId, threadId, reason);
            return;
        }

        // Drain the next batch: re-mark dispatching, record the next in-flight
        // count, save, and fire a fresh dispatcher for the new prefix.
        // Re-dispatch resolves the raw effective metadata (no unit-policy
        // re-application — matching the pre-#3031 AgentActor behaviour).
        var nextBatch = TakeBatch(channel.Messages);
        channel.Dispatching = true;
        channel.InFlightCount = nextBatch.Count;
        await SaveChannelAsync(channel, ct);

        var effective = await host.ResolveEffectiveMetadataAsync(nextBatch[0], ct);
        var cts = _activeWorkByThread.Enter(threadId);
        PendingDispatchTask = DispatchAsync(nextBatch, effective, cts.Token);
    }

    /// <summary>
    /// Dispatches a batch of pending messages for a per-thread channel as one
    /// runtime turn (#3056). When <c>concurrent_threads</c> is <c>false</c> the
    /// dispatch acquires the subject-wide lock first so concurrent threads run
    /// serialised; the lock release is in <c>finally</c> so a cancelled /
    /// failing dispatch does not pin the subject. The runtime-invocation shape
    /// itself is the host's concern (rich context for agents, lean for units).
    /// <paramref name="batch"/> is non-empty and ordered oldest-first;
    /// <c>batch[0]</c> is the representative used for thread routing.
    /// </summary>
    private async Task DispatchAsync(IReadOnlyList<Message> batch, AgentMetadata effective, CancellationToken ct)
    {
        var threadId = batch[0].ThreadId ?? string.Empty;
        var concurrent = await GetConcurrentThreadsAsync(ct);
        SemaphoreSlim? gate = concurrent ? null : (_wideLock ??= new SemaphoreSlim(1, 1));

        try
        {
            if (gate is not null)
            {
                await gate.WaitAsync(ct);
            }
            try
            {
                await host.InvokeRuntimeAsync(
                    batch,
                    effective,
                    reason => host.SignalDispatchExitAsync(threadId, reason),
                    ct);
            }
            finally
            {
                gate?.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The gate.WaitAsync may throw on cancel before the dispatcher
            // runs. Make sure the per-thread exit still runs so the channel
            // doesn't sit in a stuck Dispatching state. Other catch-paths are
            // owned by the dispatch coordinator and already invoke onDispatchExit.
            await host.SignalDispatchExitAsync(threadId, "dispatch cancelled before run");
        }
    }

    /// <summary>
    /// Coarse runtime-status snapshot derived from per-thread channel state.
    /// In-flight := channels with <c>Dispatching == true</c>; queued := total
    /// messages minus the in-flight heads; a non-dispatching channel that still
    /// holds messages is transient between drains and counts all as queued.
    /// </summary>
    public async Task<AgentRuntimeStatusReport> GetRuntimeStatusAsync(CancellationToken ct)
    {
        var threadIds = await GetChannelIndexAsync(ct);
        var inFlight = 0;
        var queued = 0;
        var channelCount = 0;

        foreach (var tid in threadIds)
        {
            var channel = await GetChannelAsync(tid, ct);
            if (channel is null)
            {
                continue;
            }

            channelCount++;
            var depth = channel.Messages.Count;
            if (channel.Dispatching)
            {
                // One in-flight batch per dispatching channel; the leading
                // InFlightCount messages are in that batch, the rest queued
                // behind it (#3056).
                inFlight++;
                queued += Math.Max(0, depth - channel.InFlightCount);
            }
            else
            {
                queued += depth;
            }
        }

        return new AgentRuntimeStatusReport(
            InFlightThreadCount: inFlight,
            QueuedMessageCount: queued,
            ChannelCount: channelCount,
            ObservedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Per-thread queue depths (thread id → message count, including the
    /// in-flight head) across all channels. Backs an agent's <c>StatusQuery</c>
    /// <c>ThreadDepths</c> projection.
    /// </summary>
    public async Task<Dictionary<string, int>> GetThreadDepthsAsync(CancellationToken ct)
    {
        var threadIds = await GetChannelIndexAsync(ct);
        var depths = new Dictionary<string, int>(threadIds.Count);
        foreach (var tid in threadIds)
        {
            var channel = await GetChannelAsync(tid, ct);
            if (channel is not null)
            {
                depths[tid] = channel.Messages.Count;
            }
        }

        return depths;
    }

    /// <summary>
    /// Per-thread cancel: cancels any dispatcher running for the thread and
    /// clears its channel so a subsequent inbound on the same thread starts a
    /// fresh drain loop. Other threads are untouched (ADR-0030 §44).
    /// </summary>
    public async Task CancelThreadAsync(string threadId, CancellationToken ct)
    {
        await _activeWorkByThread.CancelAsync(threadId);

        var channel = await GetChannelAsync(threadId, ct);
        if (channel is not null)
        {
            await RemoveChannelAsync(threadId, ct);
            logger.LogInformation(
                "Actor {ActorId} cleared channel for cancelled thread {ThreadId}",
                host.ActorId, threadId);
        }
    }

    /// <summary>
    /// Cancels the dispatcher CTS for a thread without clearing its channel —
    /// the mid-flight-amendment "stop and re-run on the same thread" path
    /// (#142), which the drain loop then resumes.
    /// </summary>
    public async ValueTask CancelDispatcherAsync(string threadId) =>
        await _activeWorkByThread.CancelAsync(threadId);

    /// <summary>
    /// Cancels every in-flight dispatcher across all threads — the
    /// authoritative-stop transition (#2981).
    /// </summary>
    public ValueTask CancelAllAsync() => _activeWorkByThread.CancelAllAsync();

    /// <summary>
    /// Resolves the subject's <c>concurrent_threads</c> policy, caching it for
    /// the actor's lifetime (the flag is not editable at runtime). Defaults to
    /// <c>true</c> (the platform default per ADR-0030 §3) when no provider is
    /// wired or the lookup fails. Exposed so an agent's prompt-context build
    /// can render the same flag it dispatches under.
    /// </summary>
    public async Task<bool> GetConcurrentThreadsAsync(CancellationToken ct)
    {
        if (_concurrentThreads.HasValue)
        {
            return _concurrentThreads.Value;
        }

        if (definitionProvider is null)
        {
            _concurrentThreads = true;
            return true;
        }

        try
        {
            var definition = await definitionProvider.GetByIdAsync(host.ActorId, ct);
            _concurrentThreads = definition?.Execution?.ConcurrentThreads ?? true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to resolve concurrent_threads for actor {ActorId}; defaulting to true.",
                host.ActorId);
            _concurrentThreads = true;
        }

        return _concurrentThreads.Value;
    }

    private async Task<ThreadChannel?> GetChannelAsync(string threadId, CancellationToken ct)
    {
        var result = await host.StateManager
            .TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, ct);
        return result.HasValue ? result.Value : null;
    }

    private async Task SaveChannelAsync(ThreadChannel channel, CancellationToken ct)
    {
        await host.StateManager.SetStateAsync(StateKeys.ChannelPrefix + channel.ThreadId, channel, ct);

        var index = await GetChannelIndexAsync(ct);
        if (!index.Contains(channel.ThreadId))
        {
            index.Add(channel.ThreadId);
            await host.StateManager.SetStateAsync(StateKeys.ChannelIndex, index, ct);
        }
    }

    private async Task RemoveChannelAsync(string threadId, CancellationToken ct)
    {
        await host.StateManager.TryRemoveStateAsync(StateKeys.ChannelPrefix + threadId, ct);

        var index = await GetChannelIndexAsync(ct);
        if (index.Remove(threadId))
        {
            if (index.Count == 0)
            {
                await host.StateManager.TryRemoveStateAsync(StateKeys.ChannelIndex, ct);
            }
            else
            {
                await host.StateManager.SetStateAsync(StateKeys.ChannelIndex, index, ct);
            }
        }
    }

    private async Task<List<string>> GetChannelIndexAsync(CancellationToken ct)
    {
        var result = await host.StateManager
            .TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, ct);
        return result.HasValue ? result.Value : new List<string>();
    }
}
