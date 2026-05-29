// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.DependencyInjection;

using Cvoya.Spring.Connector.Slack;
using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connector.Slack.Commands;
using Cvoya.Spring.Connector.Slack.Configuration;
using Cvoya.Spring.Connector.Slack.Inbound;
using Cvoya.Spring.Connector.Slack.Install;
using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Connector.Slack.Routing;
using Cvoya.Spring.Connector.Slack.Slug;
using Cvoya.Spring.Connector.Slack.WebApi;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging.Rendering;
using Cvoya.Spring.Core.Messaging.Rendering.Renderers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI registration for the Slack connector. Mirrors the GitHub
/// connector's <c>AddCvoyaSpringConnectorGitHub</c> shape: one
/// entry point bound from the supplied <see cref="IConfiguration"/>;
/// every registration uses <c>TryAdd*</c> so a cloud overlay can
/// pre-register variants without displacing the OSS defaults.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configuration section name for the Slack connector's options.
    /// </summary>
    public const string ConfigurationSectionName = "Slack";

    /// <summary>
    /// Registers every Slack-connector service: options, in-memory
    /// OAuth state store, HTTP client for Slack APIs, the OAuth
    /// service, the install store, the bound-user extractor, and the
    /// connector type itself.
    /// </summary>
    public static IServiceCollection AddCvoyaSpringConnectorSlack(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(ConfigurationSectionName);

        // OAuth options — bound from Slack:OAuth.
        services.AddOptions<SlackOAuthOptions>().Bind(section.GetSection("OAuth"));

        // Named HttpClient for every Slack OAuth/identity call. The
        // host wires the credential-health watchdog onto this name
        // per CONVENTIONS §16 / §15.
        services.AddHttpClient(SlackOAuthHttpClient.HttpClientName);
        services.AddHttpClient(SlackWebApiClient.HttpClientName);

        // Manifest-API client for the one-page portal install endpoint
        // (#2882). No credential-health watchdog — the Slack Configuration
        // Token is a short-lived, per-request operator input, not a stored
        // credential whose health we track (cf. §15).
        services.AddHttpClient(SlackManifestInstallService.HttpClientName);

        // In-memory state store is OSS-default; cloud overlays
        // substitute a Redis-backed implementation via TryAddSingleton.
        services.TryAddSingleton<ISlackOAuthStateStore, InMemorySlackOAuthStateStore>();

        services.TryAddSingleton<ISlackOAuthHttpClient, SlackOAuthHttpClient>();
        services.TryAddSingleton<ISlackInstallStore, SlackInstallStore>();
        // Per-call resolver for SlackOAuthOptions — sources credential
        // fields from the tenant → platform → env precedence chain
        // (issue #2849). Registered before SlackOAuthService so the
        // service can consume it.
        services.TryAddSingleton<ISlackOAuthOptionsResolver, SlackOAuthOptionsResolver>();
        services.TryAddSingleton<ISlackOAuthService, SlackOAuthService>();

        // Server-side install orchestrator for the one-page portal wizard
        // (#2882). Scoped — it persists tenant secrets through the
        // request-scoped secret store / registry, then reuses the OAuth
        // service to mint a state-bearing consent URL.
        services.TryAddScoped<ISlackManifestInstallService, SlackManifestInstallService>();

        // Slack-thread parent-message slug builder (ADR-0061 §4).
        // Singleton — stateless; scoped collaborators
        // (ITenantUserHumanResolver, IParticipantDisplayNameResolver)
        // are resolved per call through the scope factory.
        services.TryAddSingleton<ISlackThreadSlugBuilder, SlackThreadSlugBuilder>();

        // Slack runtime-loop services (ADR-0061 §3 / §7.2 / §7.8 / #2818).
        // All singletons; scoped collaborators resolve per call.
        services.TryAddSingleton<ISlackContainerRouter, SlackContainerRouter>();
        services.TryAddSingleton<ISlackPersonaBuilder, SlackPersonaBuilder>();
        services.TryAddSingleton<ISlackWebApiClient, SlackWebApiClient>();
        services.TryAddSingleton<ISlackOutboundDispatcher, SlackOutboundDispatcher>();

        // #2843: SlackOutboundDispatcher resolves IMessagePayloadRendererRegistry
        // to extract the message body. The Dapr platform extension registers
        // the same renderer set; we register defensively here with TryAdd so
        // standalone harnesses (and hosts that wire Slack without Dapr) still
        // resolve a working registry. Idempotent — when both extensions run,
        // the Dapr registration wins by registration order and Slack's call
        // here is a no-op.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessagePayloadRenderer, BareStringPayloadRenderer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessagePayloadRenderer, TextPropertyPayloadRenderer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessagePayloadRenderer, BodyPropertyPayloadRenderer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessagePayloadRenderer, OutputPropertyPayloadRenderer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessagePayloadRenderer, ContentPropertyPayloadRenderer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessagePayloadRenderer, A2aTaskPayloadRenderer>());
        services.TryAddSingleton<IMessagePayloadRendererRegistry, MessagePayloadRendererRegistry>();

        // Platform-side delivery wire-up (#2818) — registered as an
        // enumerable IConnectorDeliveryObserver so MessageDeliveryService
        // calls it once per successful mailbox enqueue. The observer is a
        // thin adapter onto ISlackOutboundDispatcher; the dispatcher decides
        // whether the thread has a Slack-bound participant and short-circuits
        // when it does not.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConnectorDeliveryObserver, SlackOutboundDeliveryObserver>());

        // Slack inbound-events services (#2817).
        services.TryAddSingleton<ISlackSignatureValidator, SlackSignatureValidator>();
        services.TryAddSingleton<IUnboundUserRefusalGate, InMemoryUnboundUserRefusalGate>();
        services.TryAddSingleton<ISlackInboundAuditLog, LoggerSlackInboundAuditLog>();
        services.TryAddSingleton<ISlackEventDispatcher, SlackEventDispatcher>();

        // Slash-command surface (#2819).
        services.TryAddSingleton<ISlackCommandDispatcher, SlackCommandDispatcher>();

        // Bound-user extractor — registered as an enumerable
        // ITenantBoundUserExtractor so the platform's
        // TenantConnectorBindingStore dispatches by slug (ADR-0061
        // §7.7).
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITenantBoundUserExtractor, SlackBoundUserExtractor>());

        // Register the connector type via the generic IConnectorType
        // abstraction. The host iterates every registered
        // IConnectorType at startup and calls its MapRoutes; no
        // Slack-specific code lives in the API project.
        services.AddSingleton<SlackConnectorType>();
        services.AddSingleton<IConnectorType>(sp => sp.GetRequiredService<SlackConnectorType>());

        return services;
    }
}
