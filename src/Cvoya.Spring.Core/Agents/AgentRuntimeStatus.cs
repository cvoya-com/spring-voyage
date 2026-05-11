// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Agents;

/// <summary>
/// Runtime-status label for an agent or unit actor as projected to the
/// portal (#2100). Distinct from <c>Cvoya.Spring.Dapr.Actors.AgentStatus</c>:
/// that one is the binary <c>Idle/Active</c> the actor itself reports for
/// orchestration-tool consumers; this one is the human-readable surface
/// the portal renders next to every name (engagement timeline, member
/// rosters, drawer panels, mention chips).
/// </summary>
/// <remarks>
/// Per ADR-0041 (#2099) the default <c>concurrent_threads: false</c> mode
/// serialises an agent's mailbox across threads — a long turn on one
/// thread queues sibling threads and the agent appears silent on those
/// threads until it drains. Without a visible runtime indicator every
/// silent moment looks like a defect; this enum + the
/// <c>/api/v1/tenant/{kind}/{id}/runtime-status</c> endpoint give the
/// portal the signal it needs to render the busy / queued / idle /
/// unavailable state next to the agent's name.
/// </remarks>
public enum AgentRuntimeStatus
{
    /// <summary>
    /// The actor is reachable and has nothing in flight: no per-thread
    /// channel exists or every channel has drained. The agent is ready
    /// to pick up the next inbound message.
    /// </summary>
    Idle,

    /// <summary>
    /// The actor is processing a message right now (one of its per-thread
    /// channels has a dispatcher running). May not be on the thread the
    /// user is currently looking at — the head-of-line behaviour described
    /// in ADR-0041 §"HoL scope" makes this state visible to participants
    /// in sibling threads as silence.
    /// </summary>
    Busy,

    /// <summary>
    /// The actor has at least one channel with messages queued behind
    /// the in-flight head (the HoL victim signal). Render alongside
    /// <see cref="Busy"/> when both apply; queue-only without an in-flight
    /// dispatcher is also possible briefly between drains.
    /// </summary>
    Queued,

    /// <summary>
    /// The actor's container is not running, has failed health probes, or
    /// has not yet been deployed. The portal renders this with stronger
    /// affordance than <see cref="Idle"/> so the operator sees the cause
    /// of any silence is infrastructure rather than backlog.
    /// </summary>
    Unavailable,
}
