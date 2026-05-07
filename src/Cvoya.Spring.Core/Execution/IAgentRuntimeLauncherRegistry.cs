// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Lookup seam for <see cref="IAgentRuntimeLauncher"/> strategies, keyed
/// on the <see cref="IAgentRuntimeLauncher.Kind"/> id (which matches the
/// catalogue runtime entry's <c>launcher</c> field). Per ADR-0038 the
/// dispatcher and the unit-validation workflow resolve a launcher
/// through this registry rather than walking <see cref="IAgentRuntime"/>
/// instances; per-runtime probe authoring lives next to the launcher.
/// </summary>
public interface IAgentRuntimeLauncherRegistry
{
    /// <summary>Every launcher strategy registered with the host.</summary>
    IReadOnlyList<IAgentRuntimeLauncher> All { get; }

    /// <summary>
    /// Returns the launcher whose <see cref="IAgentRuntimeLauncher.Kind"/>
    /// matches <paramref name="launcherId"/> (case-insensitive), or
    /// <c>null</c> when none is registered.
    /// </summary>
    IAgentRuntimeLauncher? Get(string launcherId);
}