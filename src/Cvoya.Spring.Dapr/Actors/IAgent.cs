// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Messaging;

using global::Dapr.Actors;

/// <summary>
/// Shared Dapr actor contract for any agent-shaped entity — a component with
/// a mailbox that receives messages and optionally returns a response.
/// <para>
/// In the Spring Voyage model a <i>unit is an agent</i>: it has the same
/// mailbox semantics and the same message-dispatch shape as a single agent,
/// with orchestration policy treated as one kind of cognition. This
/// interface captures the common "agent-shape" contract that both
/// <see cref="IAgentActor"/> and <see cref="IUnitActor"/> extend, so code
/// that only needs to talk to an agent-like thing — e.g. the message
/// router's delivery path — can do so without branching on address scheme.
/// </para>
/// <para>
/// Scheme-specific surface (members, connector binding, skills, clone
/// identity, etc.) lives on the derived interfaces. Resolving an
/// <see cref="Address"/> to a concrete <see cref="IAgent"/> proxy is the
/// job of <see cref="Routing.IAgentProxyResolver"/> — scheme-to-actor-type
/// mapping stays an internal detail of the directory/routing layer.
/// </para>
/// <para>
/// The method signature mirrors <see cref="IMessageReceiver.ReceiveAsync"/>
/// exactly so a Dapr actor proxy over <see cref="IAgent"/> is wire-compatible
/// with the proxies previously created over <see cref="IAgentActor"/> and
/// <see cref="IUnitActor"/>: Dapr dispatches actor methods by name and
/// parameter shape, which is unchanged.
/// </para>
/// </summary>
public interface IAgent : IActor
{
    /// <summary>
    /// Receives and processes a message, optionally returning a response.
    /// Implementations must not let exceptions escape the actor turn; any
    /// unexpected failure must be caught, logged, and surfaced as an error
    /// response message per the platform convention.
    /// </summary>
    /// <param name="message">The message to process.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An optional response message, or <c>null</c> if no response is needed.</returns>
    Task<Message?> ReceiveAsync(Message message, CancellationToken cancellationToken = default);
}