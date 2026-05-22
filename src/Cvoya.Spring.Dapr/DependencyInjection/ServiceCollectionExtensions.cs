// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Dapr.Units;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Extension methods for registering Spring Voyage services with the dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers core Spring Voyage abstractions. Currently a no-op placeholder that allows
    /// the host to signal intent and provides a future extension point.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringCore(this IServiceCollection services)
    {
        return services;
    }

    /// <summary>
    /// Registers all Dapr-backed implementations for routing, execution, and prompt assembly.
    /// </summary>
    /// <remarks>
    /// Both Spring Voyage hosts call this method and therefore compose the
    /// same service graph; <paramref name="role"/> is the single signal that
    /// distinguishes them. It selects which execution-side hosted services
    /// start on the host — the worker-only execution hosted services start
    /// only when <paramref name="role"/> is
    /// <see cref="SpringHostRole.ExecutionHost"/> (ADR-0052). The default
    /// <see cref="SpringHostRole.HttpFrontDoor"/> keeps existing
    /// single-argument call sites (predominantly test harnesses) compiling
    /// unchanged.
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration, used to resolve the PostgreSQL connection string.</param>
    /// <param name="role">The operational role of the host being composed.
    /// Defaults to <see cref="SpringHostRole.HttpFrontDoor"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringDapr(
        this IServiceCollection services,
        IConfiguration configuration,
        SpringHostRole role = SpringHostRole.HttpFrontDoor)
    {
        return services
            .AddCvoyaSpringInfrastructure(configuration)
            .AddCvoyaSpringRouting()
            .AddCvoyaSpringExecution(role)
            .AddCvoyaSpringInitiative()
            .AddCvoyaSpringMessaging()
            .AddCvoyaSpringStateTenancySecrets()
            .AddCvoyaSpringObservability();
    }

    /// <summary>
    /// Registers <see cref="DatabaseMigrator"/> as a hosted service so the
    /// containing host applies pending EF Core migrations to
    /// <see cref="SpringDbContext"/> on startup.
    /// </summary>
    /// <remarks>
    /// Call this from <strong>exactly one</strong> host in a deployment.
    /// In the OSS deployment that host is the Worker
    /// (<c>Cvoya.Spring.Host.Worker</c>); the API host
    /// (<c>Cvoya.Spring.Host.Api</c>) intentionally does not register the
    /// migrator. Registering it in multiple hosts that start
    /// concurrently against the same database races on DDL and one host
    /// will crash with <c>42P07: relation "..." already exists</c> (see
    /// issue #305).
    /// <para>
    /// The actual run is still gated by
    /// <see cref="DatabaseOptions.AutoMigrate"/>; operators that prefer
    /// to apply migrations out-of-band (CI/CD, scripted SQL) can leave
    /// the migrator registered and disable it via configuration.
    /// </para>
    /// <para>
    /// <see cref="DatabaseOptions"/> binding lives in
    /// <see cref="AddCvoyaSpringDapr"/> so non-migrating hosts still
    /// observe the configured value.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringDatabaseMigrator(this IServiceCollection services)
    {
        services.AddHostedService<DatabaseMigrator>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="DefaultTenantBootstrapService"/> as a hosted
    /// service so the containing host bootstraps the canonical
    /// <c>"default"</c> tenant on startup and invokes every registered
    /// <see cref="ITenantSeedProvider"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors the single-owner invariant of
    /// <see cref="AddCvoyaSpringDatabaseMigrator"/>: call this from
    /// <strong>exactly one</strong> host in a deployment. The OSS
    /// topology owns the bootstrap from the Worker (which already owns
    /// EF Core migrations); a private-cloud host that drives tenant
    /// provisioning out-of-band can leave it unregistered, or register
    /// it and gate the run via
    /// <see cref="TenancyOptions.BootstrapDefaultTenant"/>.
    /// </para>
    /// <para>
    /// Seed providers themselves are picked up via the standard DI
    /// graph — any <see cref="ITenantSeedProvider"/> registered before
    /// the hosted service starts participates in the run. The OSS
    /// providers register themselves inside
    /// <see cref="AddCvoyaSpringDapr"/>, so this extension is the only
    /// extra call a host needs to make.
    /// </para>
    /// <para>
    /// <see cref="TenancyOptions"/> binding lives in
    /// <see cref="AddCvoyaSpringDapr"/> so non-bootstrapping hosts can
    /// still observe the configured value.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringDefaultTenantBootstrap(this IServiceCollection services)
    {
        services.AddHostedService<DefaultTenantBootstrapService>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="OllamaProvider"/> as the primary <c>IAiProvider</c> when
    /// <c>LanguageModel:Ollama:Enabled=true</c> (or when the caller forces registration
    /// via <paramref name="force"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hosts call this after <see cref="AddCvoyaSpringDapr"/>. It uses <c>TryAdd</c>
    /// patterns so the private cloud host can pre-register a tenant-scoped
    /// <c>IOptions&lt;OllamaOptions&gt;</c> (for per-tenant base URLs) or an alternative
    /// <c>IAiProvider</c> wrapper, and those overrides are preserved.
    /// </para>
    /// <para>
    /// Ollama exposes an OpenAI-compatible <c>/v1/chat/completions</c> endpoint and
    /// requires no API key, so a configurable base URL is the only knob. The OSS
    /// deployment defaults to the in-cluster <c>spring-ollama</c> container; macOS
    /// GPU deployments override <c>BaseUrl</c> to the host-installed Ollama.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">Configuration root used to bind <see cref="OllamaOptions"/>.</param>
    /// <param name="force">When <c>true</c>, registers the provider regardless of the
    /// <c>Enabled</c> flag — useful for test harnesses that always want the Ollama path.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringOllamaLlm(
        this IServiceCollection services,
        IConfiguration configuration,
        bool force = false)
    {
        services.AddOptions<OllamaOptions>().BindConfiguration(OllamaOptions.SectionName);

        var enabled = force || configuration.GetValue<bool>($"{OllamaOptions.SectionName}:Enabled");
        if (!enabled)
        {
            return services;
        }

        // Clear any prior IAiProvider registration. The base AddCvoyaSpringDapr call
        // registers AnthropicProvider via AddHttpClient<IAiProvider, AnthropicProvider>();
        // when Ollama is explicitly enabled we want it to win. The cloud host can
        // pre-register its own IAiProvider BEFORE this call and it will survive because
        // we only remove AnthropicProvider-shaped registrations.
        var existing = services
            .Where(d => d.ServiceType == typeof(IAiProvider) ||
                        (d.ImplementationType == typeof(AnthropicProvider)))
            .ToList();
        foreach (var descriptor in existing)
        {
            services.Remove(descriptor);
        }

        // OllamaProvider's HttpClient flows through LlmHttpMessageHandler →
        // ILlmDispatcher. See AddCvoyaSpringDirectLlmDispatcher /
        // AddCvoyaSpringDispatcherProxiedLlm for the transport choice.
        services.AddHttpClient<IAiProvider, OllamaProvider>()
            .ConfigurePrimaryHttpMessageHandler(static sp =>
                new LlmHttpMessageHandler(sp.GetRequiredService<ILlmDispatcher>()));

        // Health-check: #616 migrated the Ollama probe into the configuration
        // validation framework. The OllamaConfigurationRequirement registers
        // as an IConfigurationRequirement; the startup validator drives the
        // probe once (matching the old OllamaHealthCheck semantics) and the
        // report surfaces the outcome consistently with every other
        // subsystem. The named HttpClient the requirement uses is registered
        // below so consumers resolving IHttpClientFactory get a configured
        // client even without AddCvoyaSpringConfigurationValidator.
        services.AddHttpClient(nameof(OllamaConfigurationRequirement));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigurationRequirement, OllamaConfigurationRequirement>());

        return services;
    }

    /// <summary>
    /// Registers the startup configuration validator (<see cref="StartupConfigurationValidator"/>)
    /// as the first <see cref="Microsoft.Extensions.Hosting.IHostedService"/>. All registered
    /// <see cref="IConfigurationRequirement"/> implementations are evaluated during
    /// <c>StartAsync</c>; a mandatory requirement reporting
    /// <see cref="ConfigurationStatus.Invalid"/> aborts host boot with its
    /// <see cref="ConfigurationRequirementStatus.FatalError"/>.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="AddCvoyaSpringDapr"/> at the appropriate point
    /// in the DI graph; exposed publicly so downstream hosts that bypass
    /// <c>AddCvoyaSpringDapr</c> (for example, a narrow CLI harness) can
    /// still opt into the validator without duplicating the registration.
    /// Uses <c>TryAdd</c> so a test harness that wants to bypass startup
    /// validation can pre-register its own no-op
    /// <see cref="IStartupConfigurationValidator"/> before this runs.
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringConfigurationValidator(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotent: skip on repeat calls. Keying off the concrete
        // StartupConfigurationValidator registration covers both the
        // singleton and the hosted-service wrapper.
        var alreadyRegistered = services.Any(d =>
            d.ServiceType == typeof(StartupConfigurationValidator));
        if (alreadyRegistered)
        {
            return services;
        }

        services.AddSingleton<StartupConfigurationValidator>();
        services.TryAddSingleton<IStartupConfigurationValidator>(
            sp => sp.GetRequiredService<StartupConfigurationValidator>());
        services.AddHostedService(sp => sp.GetRequiredService<StartupConfigurationValidator>());

        return services;
    }

    /// <summary>
    /// Registers the named <see cref="HttpClient"/> the worker uses to talk
    /// to <c>spring-dispatcher</c>, with a transport timeout sourced from
    /// <see cref="DispatcherClientOptions.RequestTimeout"/> (defaulting to
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Synchronous container runs on <c>POST /v1/containers</c> can take
    /// minutes for a Claude Code or Codex agent turn. The dispatcher
    /// already enforces the per-run deadline via
    /// <c>ContainerConfig.Timeout</c>, so an additional, shorter
    /// worker-side cap is a footgun: when the default
    /// <see cref="HttpClient.Timeout"/> of 100 s fires first the worker
    /// drops the connection, the dispatcher sees a client abort, kills
    /// the container, and the user never receives a response. This
    /// regression bit Stage 2 of #1063 / #522 in production once the
    /// argv-quoting fix let containers actually start.
    /// </para>
    /// <para>
    /// Operators who want a hard ceiling can still set
    /// <c>Dispatcher:RequestTimeout</c>; the configured value flows
    /// through <see cref="DispatcherClientOptions"/> and is applied
    /// here.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The <see cref="IHttpClientBuilder"/> so callers can layer
    /// additional handlers (telemetry, retry policies, etc.).</returns>
    internal static IHttpClientBuilder AddDispatcherHttpClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddHttpClient(DispatcherClientContainerRuntime.HttpClientName)
            .ConfigureHttpClient(static (sp, client) =>
            {
                var dispatcherOptions = sp
                    .GetRequiredService<IOptions<DispatcherClientOptions>>().Value;
                client.Timeout = dispatcherOptions.RequestTimeout
                    ?? System.Threading.Timeout.InfiniteTimeSpan;
            });
    }

    /// <summary>
    /// Registers the default <see cref="ILlmDispatcher"/> —
    /// <see cref="HttpClientLlmDispatcher"/> — which sends every LLM
    /// request directly via an injected <see cref="HttpClient"/>. The
    /// transport client is registered as a dedicated named client so it
    /// never recurses through <see cref="LlmHttpMessageHandler"/>: that
    /// would form an infinite loop because the message handler itself
    /// resolves <see cref="ILlmDispatcher"/>. Closes #1168 / ADR 0028
    /// Decision E for OSS deployments where the worker can still resolve
    /// the LLM endpoint directly (the current default — <c>spring-ollama</c>
    /// is dual-attached to <c>spring-net</c> and <c>spring-tenant-default</c>).
    /// </summary>
    /// <remarks>
    /// Idempotent. Registered transparently by
    /// <see cref="AddCvoyaSpringDapr"/> and surfaced as a public
    /// extension so test harnesses and downstream hosts can compose a
    /// bare <see cref="ILlmDispatcher"/> registration without taking the
    /// full Dapr DI graph. <c>TryAdd</c> so a downstream host that has
    /// already registered an <see cref="ILlmDispatcher"/> (for example
    /// <see cref="AddCvoyaSpringDispatcherProxiedLlm"/>) wins.
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringDirectLlmDispatcher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(HttpClientLlmDispatcher.HttpClientName, client =>
        {
            // Streaming completions and Anthropic message turns can
            // legitimately exceed the BCL HttpClient default of 100s.
            // The per-request CancellationToken passed in by the caller
            // owns the deadline.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        services.TryAddSingleton<ILlmDispatcher>(sp => new HttpClientLlmDispatcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientLlmDispatcher.HttpClientName),
            sp.GetRequiredService<ILoggerFactory>()));

        return services;
    }

    /// <summary>
    /// Swaps the default <see cref="ILlmDispatcher"/> for the
    /// dispatcher-proxied implementation
    /// (<see cref="DispatcherProxiedLlmDispatcher"/>): every LLM request
    /// is forwarded to <c>spring-dispatcher</c>'s
    /// <c>POST /v1/llm/forward</c> endpoint and the dispatcher executes
    /// the upstream call. Use this in deployments where the worker is
    /// on <c>spring-net</c> only (ADR 0028 Decision A) and the LLM
    /// endpoint lives on a tenant network the worker cannot reach
    /// directly. Closes #1168 / ADR 0028 Decision E.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hosts call this after <see cref="AddCvoyaSpringDapr"/>; the
    /// existing direct-dispatcher registration is removed before the
    /// proxied implementation is registered so there is exactly one
    /// <see cref="ILlmDispatcher"/> in the container. The dispatcher
    /// HTTP client used to forward requests is registered as a named
    /// client with the same infinite timeout and bearer-token plumbing
    /// as <see cref="AddDispatcherHttpClient"/>: long completions don't
    /// trip the BCL default, and the worker's per-deployment token
    /// flows through automatically.
    /// </para>
    /// <para>
    /// Both this and <see cref="AddCvoyaSpringDirectLlmDispatcher"/>
    /// register the <see cref="HttpClientLlmDispatcher.HttpClientName"/>
    /// named client too, because some <see cref="IAiProvider"/>
    /// implementations expose <c>HttpClient</c>-typed test seams that
    /// resolve it eagerly.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringDispatcherProxiedLlm(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Make sure DispatcherClientOptions is bound so the proxied
        // implementation has a base URL / bearer token. This mirrors the
        // pattern in AddCvoyaSpringDapr; safe to call twice.
        services.AddOptions<DispatcherClientOptions>().BindConfiguration(DispatcherClientOptions.SectionName);

        // Drop any prior ILlmDispatcher registration so we replace, not
        // append (DI resolves the *first* registration for a service
        // type, but multiple registrations with the same lifetime
        // confuse downstream observers like `IEnumerable<ILlmDispatcher>`).
        var prior = services.Where(d => d.ServiceType == typeof(ILlmDispatcher)).ToList();
        foreach (var descriptor in prior)
        {
            services.Remove(descriptor);
        }

        services.AddHttpClient(DispatcherProxiedLlmDispatcher.HttpClientName)
            .ConfigureHttpClient(static (sp, client) =>
            {
                var dispatcherOptions = sp
                    .GetRequiredService<IOptions<DispatcherClientOptions>>().Value;
                client.Timeout = dispatcherOptions.RequestTimeout
                    ?? Timeout.InfiniteTimeSpan;
            });

        services.AddSingleton<ILlmDispatcher, DispatcherProxiedLlmDispatcher>();

        return services;
    }

}
