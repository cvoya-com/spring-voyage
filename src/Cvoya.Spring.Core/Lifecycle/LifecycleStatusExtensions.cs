// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Lifecycle;

/// <summary>
/// Predicates over <see cref="LifecycleStatus"/> shared by the message-path
/// enforcement points wired in #2981 (make stop authoritative): the actor
/// receive / drain gates, the dispatcher cold-start gate, and the router
/// delivery gate all ask the same question — "is this artefact halted, so it
/// must not send, receive, or (re)launch a container?"
/// </summary>
public static class LifecycleStatusExtensions
{
    /// <summary>
    /// True when the artefact has been stopped (or is stopping) by an
    /// operator, or has errored — the states in which an authoritative stop
    /// must hold (#2981 / subsumed #2978). A halted artefact must not accept
    /// inbound domain work, must not have its drain loop re-armed, must not
    /// be cold-started from an inbound message, and must not have messages
    /// delivered to it.
    /// <para>
    /// Deliberately excludes <see cref="LifecycleStatus.Draft"/> (never
    /// started — its container lifecycle has not begun),
    /// <see cref="LifecycleStatus.Validating"/> / <see cref="LifecycleStatus.Starting"/>
    /// (mid-bring-up — work in flight is expected), and
    /// <see cref="LifecycleStatus.Unknown"/> (a read-time degraded indicator,
    /// never a persisted state — gating on it would fail closed on a transient
    /// read miss). The gates therefore fail <em>open</em> for any non-halted
    /// status, leaving the actor's own state the authority.
    /// </para>
    /// </summary>
    public static bool IsHalted(this LifecycleStatus status) =>
        status is LifecycleStatus.Stopped
            or LifecycleStatus.Stopping
            or LifecycleStatus.Error;
}
