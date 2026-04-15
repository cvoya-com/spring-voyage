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
    /// Maps address schemes to the Dapr actor type name as registered with
    /// placement. The name MUST match the concrete actor class name (e.g.
    /// <c>UnitActor</c>) used by <c>options.Actors.RegisterActor&lt;T&gt;()</c>
    /// in the worker host — placement resolves by registered class name, not
    /// by interface name.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SchemeToActorTypeName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["agent"] = nameof(AgentActor),
            ["unit"] = nameof(UnitActor),
            ["connector"] = nameof(ConnectorActor),
            ["human"] = nameof(HumanActor),
        };

    /// <inheritdoc />
    public virtual IAgent? Resolve(string scheme, string actorId)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(actorId);

        if (!SchemeToActorTypeName.TryGetValue(scheme, out var actorTypeName))
        {
            return null;
        }

        // Dapr dispatches by actor-type name + method signature, so creating
        // the proxy typed as the concrete derived interface while returning
        // it as IAgent is wire-equivalent to the pre-IAgent code path.
        return scheme.ToLowerInvariant() switch
        {
            "agent" => actorProxyFactory.CreateActorProxy<IAgentActor>(new ActorId(actorId), actorTypeName),
            "unit" => actorProxyFactory.CreateActorProxy<IUnitActor>(new ActorId(actorId), actorTypeName),
            "connector" => actorProxyFactory.CreateActorProxy<IConnectorActor>(new ActorId(actorId), actorTypeName),
            "human" => actorProxyFactory.CreateActorProxy<IHumanActor>(new ActorId(actorId), actorTypeName),
            _ => null,
        };
    }
}