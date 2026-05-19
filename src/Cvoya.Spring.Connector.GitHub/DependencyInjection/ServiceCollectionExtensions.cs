// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.DependencyInjection;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Configuration;
using Cvoya.Spring.Connector.GitHub.Labels;
using Cvoya.Spring.Connector.GitHub.RateLimit;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Configuration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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

        // Normalise the bound options — dereference a path-shaped PEM so the
        // rest of the connector sees contents, never a path. Classification
        // errors are surfaced through the IConfigurationRequirement below so
        // the startup validator can fail-fast with a unified message rather
        // than a hand-rolled throw here (#616 generalises the pre-existing
        // PostConfigure throw). Missing credentials leave options as-is;
        // the requirement reports Disabled.
        services.AddOptions<GitHubConnectorOptions>()
            .PostConfigure(static options =>
            {
                var result = GitHubAppCredentialsValidator.Classify(options);
                if (result.Classification == GitHubAppCredentialsValidator.Kind.Valid)
                {
                    options.PrivateKeyPem = result.ResolvedPrivateKeyPem!;
                }
                // Invalid / missing states are reported through
                // GitHubAppConfigurationRequirement — do NOT throw from
                // PostConfigure; the startup validator owns the abort-on-boot
                // policy so every subsystem fails the same way.
            });

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubConnectorOptions>>();
            return options.Value;
        });

        // Register the GitHub App credential requirement. Generalises the
        // pre-#616 IGitHubConnectorAvailability seam into the cross-subsystem
        // IConfigurationRequirement contract — endpoints consult
        // GitHubAppConfigurationRequirement directly for the "disabled with
        // reason" short-circuit, and the framework's report picks the same
        // status up for the /system/configuration surface.
        services.TryAddSingleton<GitHubAppConfigurationRequirement>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigurationRequirement, GitHubAppConfigurationRequirement>(
                sp => sp.GetRequiredService<GitHubAppConfigurationRequirement>()));

        // Retry / rate-limit machinery. Registered ahead of the connector
        // so GitHubConnector can depend on the tracker + options without
        // needing every host to wire them up manually. TryAdd lets consumers
        // (e.g. tests, the cloud repo) pre-register alternatives.
        services.AddOptions<GitHubRetryOptions>().Bind(section.GetSection("Retry"));
        services.TryAddSingleton(sp =>
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubRetryOptions>>().Value);

        // Rate-limit state persistence. The default is the OSS in-memory
        // store (single-host deployments); hosts that need multi-replica
        // convergence flip GitHub:RateLimit:StateStore:Backend to "dapr"
        // to ride on the platform's existing Dapr state store, or
        // pre-register their own IRateLimitStateStore implementation
        // (e.g. Redis) before calling AddCvoyaSpringConnectorGitHub.
        services.AddOptions<RateLimitStateStoreOptions>()
            .Bind(section.GetSection("RateLimit:StateStore"));
        services.TryAddSingleton<IRateLimitStateStore>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RateLimitStateStoreOptions>>();
            var backend = opts.Value.Backend;
            if (string.Equals(backend, "dapr", StringComparison.OrdinalIgnoreCase))
            {
                var daprClient = sp.GetService<global::Dapr.Client.DaprClient>()
                    ?? throw new InvalidOperationException(
                        "GitHub:RateLimit:StateStore:Backend is 'dapr' but no DaprClient is registered. Add Dapr (services.AddDaprClient()) or switch to Backend='memory'.");
                return new DaprStateBackedRateLimitStateStore(
                    daprClient,
                    opts,
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<DaprStateBackedRateLimitStateStore>());
            }

            return new InMemoryRateLimitStateStore();
        });

        services.TryAddSingleton<IGitHubRateLimitTracker>(sp => new GitHubRateLimitTracker(
            sp.GetRequiredService<GitHubRetryOptions>(),
            sp.GetRequiredService<IRateLimitStateStore>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RateLimitStateStoreOptions>>(),
            sp.GetRequiredService<ILoggerFactory>()));

        // Named HttpClient for the GitHub App JWT-backed token mint
        // (POST /app/installations/{id}/access_tokens). Declared here so the
        // host can attach the credential-health watchdog by name — the
        // connector project itself doesn't reference Cvoya.Spring.Dapr
        // (#730 / CONVENTIONS.md § 16).
        services.AddHttpClient(GitHubAppAuth.HttpClientName);

        // Named handler chain Octokit's HttpClientAdapter resolves via
        // IHttpMessageHandlerFactory in GitHubConnector.CreateHandler. The
        // built-in retry handler is registered here so DI controls its
        // lifetime; AddHttpMessageHandler requires the handler type to be
        // resolvable as transient. Declared as AddTransient (not TryAdd)
        // because IHttpClientBuilder reads the registration on every
        // handler-chain build, and TryAdd would no-op if the type was
        // pre-registered singleton by an overlay. Host.Api layers
        // AddCredentialHealthWatchdog onto the same named client so the
        // watchdog sits above the retry handler.
        services.AddTransient<GitHubRetryHandler>();
        services.AddHttpClient(GitHubConnector.OctokitHttpClientName)
            .AddHttpMessageHandler<GitHubRetryHandler>();

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

        // Installation-token cache. Options are bound from GitHub:TokenCache
        // (ProactiveRefreshWindow, CeilingTtl). The default implementation is
        // in-memory and per-host; multi-host coordination (e.g. Redis-backed)
        // is left to the private cloud repo.
        services.AddOptions<InstallationTokenCacheOptions>()
            .Bind(section.GetSection("TokenCache"));
        services.TryAddSingleton(sp =>
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<InstallationTokenCacheOptions>>().Value);
        services.TryAddSingleton<IInstallationTokenCache>(sp => new InstallationTokenCache(
            sp.GetRequiredService<InstallationTokenCacheOptions>(),
            sp.GetRequiredService<ILoggerFactory>()));

        services.TryAddSingleton<GitHubAppAuth>();

        // ADR-0047 §6: the single binding-read dispatch for outbound
        // GitHub auth. Singleton because the resolver itself holds no
        // tenant-scoped state — it captures IServiceScopeFactory so the
        // scoped ISecretResolver + ITenantContext are created per call
        // on the PAT branch.
        services.TryAddSingleton<GitHubBindingAuthResolver>();

        // OAuth App auth surface — issue #233. Registers options, storage,
        // the low-level HTTP client (named HttpClient), the orchestration
        // service, and the OAuth-authenticated client factory. All TryAdd*
        // so the cloud repo can pre-register tenant-scoped implementations
        // before AddCvoyaSpringConnectorGitHub runs.
        services.AddOptions<GitHubOAuthOptions>().Bind(section.GetSection("OAuth"));
        services.AddHttpClient(GitHubOAuthHttpClient.HttpClientName);
        services.TryAddSingleton<IOAuthStateStore, InMemoryOAuthStateStore>();
        services.TryAddSingleton<IOAuthSessionStore, InMemoryOAuthSessionStore>();
        services.TryAddSingleton<IGitHubOAuthHttpClient, GitHubOAuthHttpClient>();
        services.TryAddSingleton<IGitHubUserFetcher, OctokitGitHubUserFetcher>();
        // ADR-0047 §13: persists OAuth-issued tokens as tenant secrets
        // under the binding-scoped naming convention and refreshes the
        // calling tenant user's display identity when the flow was
        // initiated from the user-identity surface. Singleton-safe via
        // IServiceScopeFactory; the persister captures it and creates a
        // fresh scope per call so the scoped ISecretRegistry /
        // ITenantContext / ITenantUserConnectorIdentityWriter
        // dependencies activate cleanly.
        services.TryAddSingleton<IOAuthTokenPersister, OAuthTokenPersister>();
        services.TryAddSingleton<IGitHubOAuthService, GitHubOAuthService>();
        services.TryAddSingleton<IGitHubOAuthClientFactory, GitHubOAuthClientFactory>();
        // #1505: user-scope resolver for the list-repositories endpoint. Resolves
        // the caller's GitHub login + org memberships from an OAuth access token
        // so cross-tenant installation leakage can be filtered server-side.
        services.TryAddSingleton<IGitHubUserScopeResolver, OctokitGitHubUserScopeResolver>();

        services.TryAddSingleton<IWebhookSignatureValidator, WebhookSignatureValidator>();
        // PR-files fetcher (issue #2407 — path-filter coverage). Built on a
        // lazy connector accessor so the singleton graph stays acyclic:
        // GitHubWebhookHandler -> IGitHubPullRequestFilesFetcher ->
        // (Func<IGitHubConnector>) -> GitHubConnector -> GitHubWebhookHandler.
        // Resolving the connector accessor at call time breaks the cycle.
        services.TryAddSingleton<IGitHubPullRequestFilesFetcher>(sp =>
            new OctokitGitHubPullRequestFilesFetcher(
                sp.GetRequiredService<IGitHubConnector>,
                sp.GetRequiredService<ILoggerFactory>()));
        services.TryAddSingleton<GitHubWebhookHandler>();
        services.TryAddSingleton<IGitHubWebhookHandler>(sp => sp.GetRequiredService<GitHubWebhookHandler>());
        services.TryAddSingleton<GitHubConnector>();
        services.TryAddSingleton<IGitHubConnector>(sp => sp.GetRequiredService<GitHubConnector>());
        // Issue #2456 removed IGitHubWebhookRegistrar — the platform no
        // longer creates per-repo webhooks. The GitHub App's own
        // installation scope determines what GitHub delivers.

        // Installation-listing is its own abstraction (IGitHubInstallationsClient)
        // so the cloud repo can substitute a tenant-scoped implementation
        // without pulling endpoint code.
        services.TryAddSingleton<IGitHubInstallationsClient, GitHubInstallationsClient>();

        // Collaborator-listing is its own abstraction (#1133) so the cloud
        // repo can substitute a tenant-aware impl that filters by the
        // caller's permission level. The OSS default mints an installation
        // token and calls GET /repos/{owner}/{repo}/collaborators.
        services.TryAddSingleton<IGitHubCollaboratorsClient, GitHubCollaboratorsClient>();

        // No platform MCP skill registry is registered for GitHub
        // (issues #2384 / #2383): agents run gh / git directly inside their
        // container using the credentials injected by
        // GitHubConnectorRuntimeContextContributor (#2380). The
        // connector retains only auth, webhooks, lifecycle, and runtime-
        // context contribution — no github.* tools surface from
        // tools/list.

        // Register the connector via the platform-generic IConnectorType
        // abstraction. Host.Api iterates every registered IConnectorType at
        // startup and calls its MapRoutes, so no GitHub-specific code needs
        // to live in the API project.
        services.AddSingleton<GitHubConnectorType>();
        services.AddSingleton<IConnectorType>(sp => sp.GetRequiredService<GitHubConnectorType>());

        // #2380 / #2442: per-launch runtime-context AND prompt-context
        // contributor. The single GitHubConnectorRuntimeContextContributor
        // class implements both seams so the runtime env-vars and the
        // platform-layer markdown fragment stay in lockstep. The
        // dispatcher resolves both seams on every launch; the runtime
        // contributor mints credentials and stamps env-vars + a
        // binding.json file into the container, while the prompt
        // contributor returns the markdown fragment that tells the agent
        // those env-vars exist. TryAddEnumerable on both abstractions
        // so a cloud overlay can pre-register tenant-aware variants
        // without displacing the OSS defaults.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConnectorRuntimeContextContributor, GitHubConnectorRuntimeContextContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConnectorPromptContextContributor, GitHubConnectorRuntimeContextContributor>());

        // Label-roundtrip subscriber (#492): observes routed delegate
        // orchestration decisions and applies the binding's AddOnAssign /
        // RemoveOnAssign on the originating issue. Hosted as an IHostedService
        // so the subscription is set up during host start and disposed on
        // shutdown. Registered unconditionally — when IActivityEventBus isn't
        // available (older tests) the service resolution fails at activation
        // and the host refuses to start, matching the pattern of other
        // bus-coupled subscribers (ActivityEventPersister, CostTracker).
        services.AddHostedService<Labels.LabelRoutingRoundtripSubscriber>();

        return services;
    }
}
