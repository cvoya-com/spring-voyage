// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Translates a <see cref="ReflectionOutcome"/>'s action-type + action-payload
/// pair into a concrete <see cref="Message"/> envelope the actor can hand to
/// <see cref="IMessageRouter"/>. Registered against an
/// <see cref="ActionType"/> (an open string, mirroring how skills and
/// connectors surface open vocabularies) so the private cloud repo can add
/// or replace action types by registering its own handler before calling
/// <c>AddCvoyaSpring*()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Handlers are pure translators. They MUST NOT attempt to dispatch the
/// message themselves — routing is the caller's responsibility, and the caller
/// also owns the "deny via <see cref="InitiativePolicy"/>" / "deny via
/// <see cref="Policies.IUnitPolicyEnforcer"/>" gating.
/// </para>
/// <para>
/// A handler may return <c>null</c> from <see cref="TranslateAsync"/> to
/// indicate that the payload was malformed and the action should be skipped.
/// Callers surface a <c>ReflectionActionSkipped</c> activity event in that
/// case rather than throwing.
/// </para>
/// </remarks>
public interface IReflectionActionHandler
{
    /// <summary>
    /// The action-type string this handler translates. Matching is
    /// case-insensitive. Duplicate registrations are resolved in the order
    /// they are returned from DI — the first handler whose <see cref="ActionType"/>
    /// matches wins.
    /// </summary>
    string ActionType { get; }

    /// <summary>
    /// Translates the action payload into a concrete outbound message, or
    /// returns <c>null</c> when the payload cannot be translated (e.g.,
    /// missing required fields). The caller provides <paramref name="agentAddress"/>
    /// so the handler does not need to know how the actor constructs its
    /// <see cref="Address"/>.
    /// </summary>
    /// <param name="agentAddress">The address of the agent dispatching the action.</param>
    /// <param name="outcome">The reflection outcome whose action is being translated.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The translated message, or <c>null</c> if translation failed.</returns>
    Task<Message?> TranslateAsync(
        Address agentAddress,
        ReflectionOutcome outcome,
        CancellationToken cancellationToken = default);
}