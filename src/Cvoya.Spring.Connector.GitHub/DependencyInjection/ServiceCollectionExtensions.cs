// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.DependencyInjection;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Labels;
using Cvoya.Spring.Connector.GitHub.RateLimit;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Skills;

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

        // Retry / rate-limit machinery. Registered ahead of the connector
        // so GitHubConnector can depend on the tracker + options without
        // needing every host to wire them up manually. TryAdd lets consumers
        // (e.g. tests, the cloud repo) pre-register alternatives.
        services.AddOptions<GitHubRetryOptions>().Bind(section.GetSection("Retry"));
        services.TryAddSingleton(sp =>
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubRetryOptions>>().Value);
        services.TryAddSingleton<IGitHubRateLimitTracker, GitHubRateLimitTracker>();

        // Label state machine — default config matches the minimal v1 coordinator
        // protocol. Customers override via the GitHub:Labels configuration section
        // to ship their own label vocabulary.
        services.AddOptions<LabelStateMachineOptions>()
            .Bind(section.GetSection("Labels"))
            .PostConfigure(opts =>
            {
                // If the configuration section is missing or empty, fall back to
                // the OSS default. Presence is indicated by a non-empty States list.
                if (opts.States.Count == 0 && opts.Transitions.Count == 0 && string.IsNullOrWhiteSpace(opts.InitialState))
                {
                    var defaults = LabelStateMachineOptions.Default();
                    opts.States = defaults.States;
                    opts.Transitions = defaults.Transitions;
                    opts.InitialState = defaults.InitialState;
                }
            });
        services.TryAddSingleton<LabelStateMachine>(sp =>
            new LabelStateMachine(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LabelStateMachineOptions>>().Value));

        services.TryAddSingleton<GitHubAppAuth>();
        services.TryAddSingleton<IWebhookSignatureValidator, WebhookSignatureValidator>();
        services.TryAddSingleton<GitHubWebhookHandler>();
        services.TryAddSingleton<IGitHubWebhookHandler>(sp => sp.GetRequiredService<GitHubWebhookHandler>());
        services.TryAddSingleton<GitHubConnector>();
        services.TryAddSingleton<IGitHubConnector>(sp => sp.GetRequiredService<GitHubConnector>());
        services.TryAddSingleton<GitHubSkillRegistry>();
        services.TryAddSingleton<IGitHubWebhookRegistrar, GitHubWebhookRegistrar>();
        // Installation-listing is its own abstraction (IGitHubInstallationsClient)
        // so the cloud repo can substitute a tenant-scoped implementation
        // without pulling endpoint code.
        services.TryAddSingleton<IGitHubInstallationsClient, GitHubInstallationsClient>();

        // Expose the GitHub skills through the cross-connector ISkillRegistry abstraction
        // so the MCP server (and any future planner) can discover them uniformly.
        services.AddSingleton<ISkillRegistry>(sp => sp.GetRequiredService<GitHubSkillRegistry>());

        // Register the connector via the platform-generic IConnectorType
        // abstraction. Host.Api iterates every registered IConnectorType at
        // startup and calls its MapRoutes, so no GitHub-specific code needs
        // to live in the API project.
        services.AddSingleton<GitHubConnectorType>();
        services.AddSingleton<IConnectorType>(sp => sp.GetRequiredService<GitHubConnectorType>());

        return services;
    }
}