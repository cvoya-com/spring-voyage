// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv.DependencyInjection;

using Cvoya.Spring.Connector.Arxiv.Skills;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Extension methods for registering arxiv connector services with the
/// dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The configuration section name for arxiv connector options.
    /// </summary>
    public const string ConfigurationSectionName = "Arxiv";

    /// <summary>
    /// Registers the arxiv connector, its default HTTP client, the skill
    /// registry, and the generic <see cref="IConnectorType"/> binding. All
    /// registrations use <c>TryAdd*</c> so a host (or the private cloud repo)
    /// can pre-register alternative implementations without being overwritten.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration — bound into <see cref="ArxivConnectorOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringConnectorArxiv(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ArxivConnectorOptions>()
            .Bind(configuration.GetSection(ConfigurationSectionName));

        services.AddHttpClient(ArxivClient.HttpClientName, client =>
        {
            // arxiv publishes a soft usage guide requesting a descriptive UA.
            // See https://info.arxiv.org/help/api/tou.html.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "CvoyaSpring-Arxiv-Connector/1.0 (+https://github.com/cvoya-com/spring-voyage)");
        });

        services.TryAddSingleton<IArxivClient, ArxivClient>();

        services.TryAddSingleton<SearchLiteratureSkill>();
        services.TryAddSingleton<FetchAbstractSkill>();

        services.TryAddSingleton<ArxivSkillRegistry>();
        services.AddSingleton<ISkillRegistry>(sp => sp.GetRequiredService<ArxivSkillRegistry>());

        services.TryAddSingleton<ArxivConnectorType>();
        services.AddSingleton<IConnectorType>(sp => sp.GetRequiredService<ArxivConnectorType>());

        return services;
    }
}