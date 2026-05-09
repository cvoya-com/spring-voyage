// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.DependencyInjection;

using Cvoya.Spring.AgentRuntimes.Launchers;
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
    /// Registers the v0.1 closed set of <see cref="IAgentRuntimeLauncher"/>
    /// strategies (<see cref="ClaudeCodeLauncher"/>,
    /// <see cref="CodexLauncher"/>, <see cref="GeminiLauncher"/>,
    /// <see cref="SpringVoyageAgentLauncher"/>) plus the default
    /// <see cref="IAgentRuntimeLauncherRegistry"/>. The runtime catalogue's
    /// per-runtime <c>launcher</c> field selects which strategy services
    /// each invocation.
    /// </summary>
    public static IServiceCollection AddCvoyaSpringAgentRuntimes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentRuntimeLauncher, ClaudeCodeLauncher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentRuntimeLauncher, CodexLauncher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentRuntimeLauncher, GeminiLauncher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentRuntimeLauncher, SpringVoyageAgentLauncher>());

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
