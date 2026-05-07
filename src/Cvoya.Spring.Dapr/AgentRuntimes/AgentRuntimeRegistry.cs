// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.AgentRuntimes;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Catalog;

/// <summary>
/// Catalogue-backed <see cref="IAgentRuntimeRegistry"/> implementation.
/// Synthesises a legacy <see cref="IAgentRuntime"/> projection over each
/// runtime entry in <see cref="IRuntimeCatalog.AgentRuntimes"/> via
/// <see cref="CatalogAgentRuntimeAdapter"/>. Per ADR-0038 the per-provider
/// runtime classes are gone — runtimes are platform configuration.
/// </summary>
/// <remarks>
/// <para>
/// This registry exists only to keep the legacy host-side interface
/// surface working until PR-1b reshapes the wire DTOs. New code SHOULD
/// consume <see cref="IRuntimeCatalog"/> directly. See follow-up issue
/// for full retirement of <see cref="IAgentRuntime"/> and
/// <see cref="IAgentRuntimeRegistry"/> after the wire reshape.
/// </para>
/// <para>
/// <see cref="Get(string)"/> matches case-insensitively on
/// <see cref="Cvoya.Spring.Core.Catalog.AgentRuntime.Id"/>.
/// </para>
/// </remarks>
public class AgentRuntimeRegistry : IAgentRuntimeRegistry
{
    private readonly IReadOnlyList<IAgentRuntime> _runtimes;
    private readonly Dictionary<string, IAgentRuntime> _byId;

    /// <summary>
    /// Creates a new registry from the catalogue. Constructed once at host
    /// startup; the catalogue is loaded from <c>platform/runtime-catalog.yaml</c>.
    /// </summary>
    public AgentRuntimeRegistry(IRuntimeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _runtimes = catalog.AgentRuntimes
            .Select(r => (IAgentRuntime)new CatalogAgentRuntimeAdapter(r, catalog))
            .ToArray();
        _byId = _runtimes.ToDictionary(r => r.Id, r => r, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyList<IAgentRuntime> All => _runtimes;

    /// <inheritdoc />
    public IAgentRuntime? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _byId.TryGetValue(id, out var runtime) ? runtime : null;
    }
}
