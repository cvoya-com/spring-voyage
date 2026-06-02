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
    /// Invokes the subject's runtime for the head message. The engine supplies
    /// the per-thread <paramref name="onDispatchExit"/> callback the runtime
    /// pipeline must invoke when the dispatcher returns, so the engine can
    /// drain the thread's queue. Agents build a rich
    /// <c>PromptAssemblyContext</c> from <paramref name="effective"/>; units
    /// use the lean overload and ignore it.
    /// </summary>
    Task InvokeRuntimeAsync(
        Message head,
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
            dispatch: (channel, eff, _) =>
            {
                var cts = _activeWorkByThread.Enter(channel.ThreadId);
                PendingDispatchTask = DispatchAsync(channel.Messages[0], eff, cts.Token);
                return Task.CompletedTask;
            },
            emitActivity: host.EmitActivityAsync,
            cancellationToken: ct);
    }

    /// <summary>
    /// Advances the per-thread queue when a dispatcher returns (the actor's
    /// <c>OnDispatchExitAsync</c> calls this on a turn). Removes the dispatched
    /// head; on an authoritative stop, drops the remaining backlog and removes
    /// the channel; when the queue is empty, removes the channel; otherwise
    /// re-arms the dispatcher for the new head.
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

        // Remove the dispatched head; appends queued behind it, so removing the
        // head preserves per-thread FIFO for the remaining queue.
        if (channel.Messages.Count > 0)
        {
            channel.Messages.RemoveAt(0);
        }

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

        // Drain the next queued message: re-mark dispatching, save, and fire a
        // fresh dispatcher for the new head. Re-dispatch resolves the raw
        // effective metadata (no unit-policy re-application — matching the
        // pre-#3031 AgentActor behaviour).
        channel.Dispatching = true;
        await SaveChannelAsync(channel, ct);

        var head = channel.Messages[0];
        var effective = await host.ResolveEffectiveMetadataAsync(head, ct);
        var cts = _activeWorkByThread.Enter(threadId);
        PendingDispatchTask = DispatchAsync(head, effective, cts.Token);
    }

    /// <summary>
    /// Dispatches a single message for a per-thread channel. When
    /// <c>concurrent_threads</c> is <c>false</c> the dispatch acquires the
    /// subject-wide lock first so concurrent threads run serialised; the lock
    /// release is in <c>finally</c> so a cancelled / failing dispatch does not
    /// pin the subject. The runtime-invocation shape itself is the host's
    /// concern (rich context for agents, lean for units).
    /// </summary>
    private async Task DispatchAsync(Message head, AgentMetadata effective, CancellationToken ct)
    {
        var threadId = head.ThreadId ?? string.Empty;
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
                    head,
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
                inFlight++;
                queued += Math.Max(0, depth - 1);
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
