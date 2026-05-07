// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Collections.Generic;
using System.Linq;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// Default <see cref="IAiProviderRegistry"/> implementation. Builds a
/// case-insensitive id → provider map at construction time from every
/// <see cref="IAiProvider"/> registered in DI. Two providers reporting
/// the same <see cref="IAiProvider.Id"/> fail the constructor with a
/// duplicate-id message — surfacing the misconfiguration at host start
/// instead of on first dispatch.
/// </summary>
/// <remarks>
/// Pairs with <c>IAgentRuntimeLauncherRegistry</c>: a thin enumerator
/// over a DI-registered set, owning no state of its own. Registered as
/// a singleton; consumers in actor / orchestration code resolve through
/// the registry rather than directly injecting <see cref="IAiProvider"/>.
/// </remarks>
public sealed class AiProviderRegistry : IAiProviderRegistry
{
    private readonly Dictionary<string, IAiProvider> _byId;
    private readonly IReadOnlyList<IAiProvider> _all;

    /// <summary>
    /// Creates a registry over every <see cref="IAiProvider"/> registered
    /// in DI. Throws when two providers share the same <see cref="IAiProvider.Id"/>
    /// — that's a deployment-time misconfiguration, not a runtime
    /// recoverable state.
    /// </summary>
    public AiProviderRegistry(IEnumerable<IAiProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _byId = new Dictionary<string, IAiProvider>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<IAiProvider>();
        foreach (var provider in providers)
        {
            if (provider is null)
            {
                continue;
            }
            var id = provider.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException(
                    $"AI provider '{provider.GetType().FullName}' has a null/blank Id; every IAiProvider must declare a stable id.");
            }
            if (_byId.ContainsKey(id))
            {
                throw new InvalidOperationException(
                    $"Duplicate IAiProvider registration for id '{id}': '{_byId[id].GetType().FullName}' and '{provider.GetType().FullName}'. " +
                    "Each provider id must be unique within the deployment.");
            }
            _byId[id] = provider;
            ordered.Add(provider);
        }
        _all = ordered;
    }

    /// <inheritdoc />
    public IAiProvider? Get(string providerId) =>
        string.IsNullOrWhiteSpace(providerId)
            ? null
            : _byId.TryGetValue(providerId, out var provider) ? provider : null;

    /// <inheritdoc />
    public IReadOnlyList<IAiProvider> All => _all;
}