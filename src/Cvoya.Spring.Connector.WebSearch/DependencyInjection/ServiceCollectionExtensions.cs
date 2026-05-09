// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch.DependencyInjection;

using Cvoya.Spring.Connector.WebSearch.Providers;
using Cvoya.Spring.Connector.WebSearch.Skills;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Extension methods for registering the web-search connector and its default
/// provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configuration section name for web-search options.
    /// </summary>
    public const string ConfigurationSectionName = "WebSearch";

    /// <summary>
    /// Registers the web-search connector, the default Brave provider, the
    /// skill registry, and the generic <see cref="IConnectorType"/> binding.
    /// Hosts (or the private cloud repo) can slot in additional
    /// <see cref="IWebSearchProvider"/> implementations BEFORE calling this
    /// method; because this method uses <c>AddSingleton</c> for the provider
    /// set and <c>TryAddSingleton</c> on the per-type singletons, additional
    /// providers stack instead of being overwritten. The connector type also
    /// rejects bind requests whose provider is not registered so drift
    /// between config and DI surfaces as a 400 rather than a silent failure.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">Application configuration — bound into <see cref="WebSearchConnectorOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringConnectorWebSearch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<WebSearchConnectorOptions>()
            .Bind(configuration.GetSection(ConfigurationSectionName));

        // Default provider: Brave. Registered as AddSingleton (not TryAdd*) so
        // the DI collection can hold multiple IWebSearchProvider
        // implementations side by side — the connector picks among them using
        // the unit's config.
        services.AddHttpClient(BraveSearchProvider.HttpClientName);
        services.AddSingleton<IWebSearchProvider, BraveSearchProvider>();

        services.TryAddSingleton<WebSearchSkill>();

        services.TryAddSingleton<WebSearchSkillRegistry>();
        services.AddSingleton<ISkillRegistry>(sp =>
            sp.GetRequiredService<WebSearchSkillRegistry>());

        services.TryAddSingleton<WebSearchConnectorType>();
        services.AddSingleton<IConnectorType>(sp =>
            sp.GetRequiredService<WebSearchConnectorType>());

        return services;
    }
}
