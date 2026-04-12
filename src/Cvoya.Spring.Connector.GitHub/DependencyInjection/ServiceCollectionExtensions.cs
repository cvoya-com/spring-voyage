// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.DependencyInjection;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Webhooks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Extension methods for registering GitHub connector services with the dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The configuration section name for GitHub connector options.
    /// </summary>
    public const string ConfigurationSectionName = "GitHub";

    /// <summary>
    /// Registers all GitHub connector services including authentication, webhook handling,
    /// skill registry, and the connector itself.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration, used to bind GitHub connector options.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringConnectorGitHub(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(ConfigurationSectionName);
        services.AddOptions<GitHubConnectorOptions>().Bind(section);

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubConnectorOptions>>();
            return options.Value;
        });

        services.TryAddSingleton<GitHubAppAuth>();
        services.TryAddSingleton<GitHubWebhookHandler>();
        services.TryAddSingleton<GitHubSkillRegistry>();
        services.TryAddSingleton<GitHubConnector>();

        return services;
    }
}