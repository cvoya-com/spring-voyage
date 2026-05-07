// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.ModelProviders;

/// <summary>
/// Lookup seam for <see cref="IModelProviderAdapter"/> strategies. The
/// default DI implementation enumerates every registered
/// <see cref="IModelProviderAdapter"/> and resolves by
/// <see cref="IModelProviderAdapter.AdapterId"/>.
/// </summary>
public interface IModelProviderAdapterRegistry
{
    /// <summary>Every adapter registered with the host.</summary>
    IReadOnlyList<IModelProviderAdapter> All { get; }

    /// <summary>
    /// Returns the adapter whose <see cref="IModelProviderAdapter.AdapterId"/>
    /// matches <paramref name="adapterId"/> (case-insensitive), or
    /// <c>null</c> when none is registered.
    /// </summary>
    IModelProviderAdapter? Get(string adapterId);
}
