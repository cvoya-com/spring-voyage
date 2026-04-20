// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Ollama.DependencyInjection;

using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Extension methods that register the Ollama agent runtime with the host's
/// dependency-injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="OllamaAgentRuntime"/> as an additional
    /// <see cref="IAgentRuntime"/> alongside any other runtimes already in
    /// the container, binds <see cref="OllamaAgentRuntimeOptions"/> to the
    /// <c>AgentRuntimes:Ollama</c> configuration section, and configures
    /// the named <see cref="HttpClient"/> the runtime uses for its
    /// <c>/api/tags</c> reachability probe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All registrations except the agent-runtime enumeration use
    /// <c>TryAdd*</c> so the private cloud host (or another downstream
    /// composition) can pre-register tenant-scoped variants and have them
    /// preserved.
    /// </para>
    /// <para>
    /// Multiple agent runtimes coexist in the same DI container by being
    /// registered as additional <see cref="IAgentRuntime"/> services — the
    /// default <c>IAgentRuntimeRegistry</c> implementation enumerates every
    /// registration. Calling this method more than once would register
    /// duplicates, so it guards against re-entry by checking for an
    /// existing <see cref="OllamaAgentRuntime"/>-shaped registration.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration root used to bind <see cref="OllamaAgentRuntimeOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringAgentRuntimeOllama(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OllamaAgentRuntimeOptions>()
            .Bind(configuration.GetSection(OllamaAgentRuntimeOptions.SectionName));

        services.AddHttpClient(OllamaAgentRuntime.HttpClientName);

        // Idempotent: skip on repeat calls. Composite hosts that wire DI
        // through multiple entry points should not double-register the
        // runtime — otherwise the registry would surface two "ollama"
        // entries and Get(string) would return the first, which is a
        // confusing failure mode.
        var alreadyRegistered = services.Any(d =>
            d.ServiceType == typeof(IAgentRuntime) &&
            d.ImplementationType == typeof(OllamaAgentRuntime));

        if (!alreadyRegistered)
        {
            services.AddSingleton<IAgentRuntime, OllamaAgentRuntime>();
        }

        // Expose the runtime by its concrete type too so a host that needs
        // to inject the strongly-typed runtime (e.g. for an admin endpoint
        // that wants to surface the seed file) can resolve it without
        // walking the registry. TryAdd preserves a downstream override.
        services.TryAddSingleton<OllamaAgentRuntime>();

        return services;
    }
}