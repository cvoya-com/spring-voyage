// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.DependencyInjection;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI extension methods for the consolidated agent-runtime launcher
/// surface (ADR-0038).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default <see cref="IAgentRuntimeLauncherRegistry"/>.
    /// The actual launcher implementations
    /// (<c>ClaudeCodeLauncher</c>, <c>CodexLauncher</c>,
    /// <c>GeminiLauncher</c>, <c>SpringVoyageAgentLauncher</c>) are
    /// registered by <c>AddCvoyaSpringDapr</c> until Chunk 2 of PR-1a
    /// physically relocates them into this project.
    /// </summary>
    public static IServiceCollection AddCvoyaSpringAgentRuntimes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAgentRuntimeLauncherRegistry, AgentRuntimeLauncherRegistry>();
        return services;
    }
}

/// <summary>
/// Default DI-driven implementation of
/// <see cref="IAgentRuntimeLauncherRegistry"/>.
/// </summary>
internal sealed class AgentRuntimeLauncherRegistry : IAgentRuntimeLauncherRegistry
{
    private readonly Dictionary<string, IAgentRuntimeLauncher> _byId;

    public AgentRuntimeLauncherRegistry(IEnumerable<IAgentRuntimeLauncher> launchers)
    {
        ArgumentNullException.ThrowIfNull(launchers);
        var list = launchers.ToList();
        All = list;
        _byId = list.ToDictionary(l => l.Kind, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IAgentRuntimeLauncher> All { get; }

    public IAgentRuntimeLauncher? Get(string launcherId)
    {
        if (string.IsNullOrWhiteSpace(launcherId))
        {
            return null;
        }
        return _byId.GetValueOrDefault(launcherId);
    }
}