// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Routing;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Resolves an <see cref="Address"/>-like coordinate to an <see cref="IAgent"/>
/// Dapr actor proxy. Encapsulates the scheme-to-actor-type mapping
/// (<c>agent://</c> → <see cref="IAgentActor"/>, <c>unit://</c> →
/// <see cref="IUnitActor"/>, <c>human://</c> → <see cref="IHumanActor"/>,
/// <c>connector://</c> → <see cref="IConnectorActor"/>) so callers that only
/// need the shared mailbox contract — e.g. the message router's delivery
/// path — do not have to branch on scheme.
/// <para>
/// Scheme-specific resolution stays an internal detail of this layer. Code
/// that needs a scheme-specific surface (member management on units, skills
/// on agents, …) continues to resolve directly through
/// <see cref="global::Dapr.Actors.Client.IActorProxyFactory"/>.
/// </para>
/// </summary>
public interface IAgentProxyResolver
{
    /// <summary>
    /// Returns an <see cref="IAgent"/> proxy for the actor identified by
    /// <paramref name="scheme"/> and <paramref name="actorId"/>, or
    /// <c>null</c> if <paramref name="scheme"/> is not a recognised
    /// agent-shaped scheme.
    /// </summary>
    /// <param name="scheme">The address scheme (e.g. <c>agent</c>, <c>unit</c>, <c>human</c>, <c>connector</c>). Comparison is case-insensitive.</param>
    /// <param name="actorId">The Dapr actor identifier.</param>
    /// <returns>An <see cref="IAgent"/> proxy, or <c>null</c> if the scheme is unknown.</returns>
    IAgent? Resolve(string scheme, string actorId);
}