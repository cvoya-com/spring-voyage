// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Routing;

using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

/// <summary>
/// Default <see cref="IAgentProxyResolver"/> that maps each known
/// agent-shaped scheme to its corresponding Dapr actor interface and
/// creates a proxy typed as <see cref="IAgent"/>. Because every
/// scheme-specific actor interface (<see cref="IAgentActor"/>,
/// <see cref="IUnitActor"/>, <see cref="IHumanActor"/>,
/// <see cref="IConnectorActor"/>) now extends <see cref="IAgent"/>, the
/// Dapr-generated proxy satisfies the shared mailbox contract and the
/// scheme-specific surface stays out of the router.
/// </summary>
public class AgentProxyResolver(IActorProxyFactory actorProxyFactory) : IAgentProxyResolver
{
    /// <summary>
    /// Maps address schemes to the corresponding Dapr actor interface type.
    /// The proxy is always created over the concrete derived interface so
    /// Dapr invokes the right actor registration; <see cref="IAgent"/>
    /// provides the shared mailbox contract the caller needs.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Type> SchemeToActorType =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["agent"] = typeof(IAgentActor),
            ["unit"] = typeof(IUnitActor),
            ["connector"] = typeof(IConnectorActor),
            ["human"] = typeof(IHumanActor),
        };

    /// <inheritdoc />
    public virtual IAgent? Resolve(string scheme, string actorId)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(actorId);

        if (!SchemeToActorType.TryGetValue(scheme, out var actorType))
        {
            return null;
        }

        // Dapr dispatches by actor-type name + method signature, so creating
        // the proxy typed as the concrete derived interface while returning
        // it as IAgent is wire-equivalent to the pre-IAgent code path.
        return scheme.ToLowerInvariant() switch
        {
            "agent" => actorProxyFactory.CreateActorProxy<IAgentActor>(new ActorId(actorId), actorType.Name),
            "unit" => actorProxyFactory.CreateActorProxy<IUnitActor>(new ActorId(actorId), actorType.Name),
            "connector" => actorProxyFactory.CreateActorProxy<IConnectorActor>(new ActorId(actorId), actorType.Name),
            "human" => actorProxyFactory.CreateActorProxy<IHumanActor>(new ActorId(actorId), actorType.Name),
            _ => null,
        };
    }
}