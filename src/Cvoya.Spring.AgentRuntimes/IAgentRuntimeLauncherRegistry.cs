// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// Lookup seam for <see cref="IAgentRuntimeLauncher"/> strategies, keyed
/// on the <see cref="IAgentRuntimeLauncher.Kind"/> id (which matches the
/// catalogue entry's <c>launcher</c> field). Replaces the previous
/// scan-by-kind logic in <see cref="IAgentDispatchCoordinator"/> per
/// ADR-0038's "register by launcher id from the catalogue" rule.
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
