// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tracks ephemeral agent containers that the unified A2A dispatch path
/// stands up for the duration of a single conversation turn. Where
/// <see cref="PersistentAgentRegistry"/> owns long-lived per-agent containers,
/// this registry owns short-lived per-(agent, conversation) entries: the
/// dispatcher registers an entry when it starts the container, releases it
/// when the turn drains (success, failure, or cancellation), and the host's
/// graceful shutdown sweep stops anything still tracked.
/// </summary>
/// <remarks>
/// <para>
/// PR 5 of the #1087 series. The unified dispatch path no longer relies on
/// <see cref="IContainerRuntime.RunAsync"/> to start, run-to-completion, and
/// reap an ephemeral agent in a single call. Instead it starts the container
/// in detached mode (<see cref="IContainerRuntime.StartAsync"/>), talks to it
/// over A2A, and tears it down explicitly. This registry exists so the host
/// has a single place to observe and stop ephemeral containers — without it
/// a misbehaving agent that ignores cancellation could outlive the dispatch
/// process and leak.
/// </para>
/// <para>
/// Entries are keyed by a synthetic <c>(agentId, conversationId, lease)</c>
/// composite — multiple parallel turns against the same persistent agent are
/// not a thing today (the actor serialises turns), but the same
/// <c>(agentId, conversationId)</c> pair can recur after a turn completes,
/// so we layer a per-call lease id on top to keep entries disjoint.
/// </para>
/// </remarks>
public class EphemeralAgentRegistry(
    IContainerRuntime containerRuntime,
    ILoggerFactory loggerFactory) : IHostedService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<EphemeralAgentRegistry>();
    private readonly ConcurrentDictionary<string, EphemeralAgentEntry> _entries = new();

    /// <summary>
    /// Default grace period the SIGTERM → SIGKILL teardown waits before
    /// escalating. Mirrors the bridge's <c>AGENT_CANCEL_GRACE_MS</c> default
    /// in <c>deployment/agent-sidecar/src/config.ts</c>.
    /// </summary>
    public static readonly TimeSpan DefaultCancellationGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Registers an ephemeral container. The returned lease must be passed to
    /// <see cref="ReleaseAsync"/> when the turn ends so the entry is removed
    /// from the registry and the container is torn down.
    /// </summary>
    public EphemeralAgentLease Register(string agentId, string conversationId, string containerId)
    {
        var lease = $"{agentId}|{conversationId}|{Guid.NewGuid():N}";
        _entries[lease] = new EphemeralAgentEntry(
            AgentId: agentId,
            ConversationId: conversationId,
            ContainerId: containerId,
            StartedAt: DateTimeOffset.UtcNow);

        _logger.LogDebug(
            "Ephemeral agent {AgentId} (conversation {ConversationId}) registered as container {ContainerId}",
            agentId, conversationId, containerId);

        return new EphemeralAgentLease(lease);
    }

    /// <summary>
    /// Removes the entry for the given lease and stops the underlying
    /// container. Idempotent — a second call with the same lease is a no-op.
    /// </summary>
    public async Task ReleaseAsync(EphemeralAgentLease lease, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryRemove(lease.Token, out var entry))
        {
            return;
        }

        _logger.LogDebug(
            "Releasing ephemeral agent {AgentId} (conversation {ConversationId}, container {ContainerId})",
            entry.AgentId, entry.ConversationId, entry.ContainerId);

        try
        {
            await containerRuntime.StopAsync(entry.ContainerId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to stop ephemeral container {ContainerId} for agent {AgentId}",
                entry.ContainerId, entry.AgentId);
        }
    }

    /// <summary>
    /// Returns a snapshot of currently-tracked entries. Used by tests and
    /// diagnostics; not part of the dispatcher hot path.
    /// </summary>
    public IReadOnlyCollection<EphemeralAgentEntry> GetAllEntries()
    {
        return _entries.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_entries.IsEmpty)
        {
            return;
        }

        _logger.LogInformation(
            "Ephemeral agent registry shutting down — stopping {Count} tracked container(s)",
            _entries.Count);

        var leases = _entries.Keys.ToList();
        var tasks = leases.Select(l => ReleaseAsync(new EphemeralAgentLease(l), cancellationToken));
        await Task.WhenAll(tasks);
    }
}

/// <summary>
/// Opaque lease handle identifying an ephemeral agent container in
/// <see cref="EphemeralAgentRegistry"/>. The dispatcher gets the lease back
/// from <see cref="EphemeralAgentRegistry.Register"/> and hands it to
/// <see cref="EphemeralAgentRegistry.ReleaseAsync"/> at the end of the turn.
/// </summary>
public readonly record struct EphemeralAgentLease(string Token);

/// <summary>
/// Tracked entry inside <see cref="EphemeralAgentRegistry"/>.
/// </summary>
public record EphemeralAgentEntry(
    string AgentId,
    string ConversationId,
    string ContainerId,
    DateTimeOffset StartedAt);